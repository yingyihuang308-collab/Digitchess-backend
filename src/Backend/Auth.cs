using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Backend;
public sealed record RegisterInput(string? LoginName, string? Password, string? Nickname);
public sealed record LoginInput(string? LoginName, string? Password);
public sealed record RefreshInput(string? RefreshToken);
public sealed record NicknameInput(string? Nickname);
public sealed class AuthKeys : IDisposable
{
    private readonly RSA rsa = RSA.Create();
    public RsaSecurityKey Key { get; }
    public string Issuer { get; }
    public string Audience { get; }
    public AuthKeys(IConfiguration config)
    {
        var pem = config["Auth:PrivateKeyPem"];
        var path = config["Auth:PrivateKeyPath"];
        if (!string.IsNullOrWhiteSpace(path)) pem = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(pem)) throw new InvalidOperationException("Configure Auth__PrivateKeyPath or Auth__PrivateKeyPem; no default signing key exists.");
        rsa.ImportFromPem(pem);
        _ = rsa.ExportParameters(true);
        Key = new RsaSecurityKey(rsa) { KeyId = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))[..16] };
        Issuer = config["Auth:Issuer"] ?? "digitchess";
        Audience = config["Auth:Audience"] ?? "digitchess-client";
    }
    public TokenValidationParameters Validation => new() {
        ValidateIssuerSigningKey = true, IssuerSigningKey = Key, ValidateIssuer = true, ValidIssuer = Issuer,
        ValidateAudience = true, ValidAudience = Audience, ValidateLifetime = true, RequireExpirationTime = true,
        RequireSignedTokens = true, ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ClockSkew = TimeSpan.FromSeconds(10)
    };
    public string Access(Player u) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        Issuer, Audience, [new Claim("sub", u.Id.ToString()), new Claim("jti", Guid.NewGuid().ToString())],
        notBefore: DateTime.UtcNow, expires: DateTime.UtcNow.AddMinutes(10),
        signingCredentials: new SigningCredentials(Key, SecurityAlgorithms.RsaSha256)));
    public void Dispose() => rsa.Dispose();
}
public sealed class Accounts(GameDb db, AuthKeys keys, PasswordHasher<Player> hasher)
{
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static object Profile(Player p) => new { id = p.Id, loginName = p.LoginName, nickname = p.Nickname, createdAt = p.CreatedAt };
    public static string Normalize(string? name) => (name ?? "").Trim().ToUpperInvariant();
    public async Task<object> Register(RegisterInput input)
    {
        var name = Check.Text(input.LoginName, 3, 32, "登录名");
        Check.That(Regex.IsMatch(name, "^[A-Za-z0-9_]+$"), 400, "invalid_login", "登录名仅允许英文字母、数字和下划线");
        Check.That(input.Password is { Length: >= 12 and <= 128 }, 400, "invalid_password", "密码长度须为12至128个字符");
        var u = new Player { LoginName = name, NormalizedLogin = Normalize(name), Nickname = Check.Text(input.Nickname, 1, 32, "昵称") };
        u.PasswordHash = hasher.HashPassword(u, input.Password!);
        db.Users.Add(u);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" })
        { throw new ApiError(409, "login_taken", "登录名已被使用"); }
        return Profile(u);
    }
    // Dummy hash equalizes the expensive part of unknown-user and wrong-password paths.
    private static readonly Player Dummy = new();
    private static readonly string DummyHash = new PasswordHasher<Player>().HashPassword(Dummy, "dummy-only-not-a-user-password");
    public async Task<object> Login(LoginInput input)
    {
        Check.That(input.Password is { Length: > 0 and <= 128 } && (input.LoginName?.Length ?? 0) <= 64, 401, "invalid_credentials", "账号或密码错误");
        var u = await db.Users.SingleOrDefaultAsync(x => x.NormalizedLogin == Normalize(input.LoginName));
        var result = hasher.VerifyHashedPassword(u ?? Dummy, u?.PasswordHash ?? DummyHash, input.Password!);
        Check.That(u is { Disabled: false } && result != PasswordVerificationResult.Failed, 401, "invalid_credentials", "账号或密码错误");
        if (result == PasswordVerificationResult.SuccessRehashNeeded) u!.PasswordHash = hasher.HashPassword(u, input.Password!);
        var (session, token) = NewSession(u!.Id, null);
        db.RefreshSessions.Add(session); await db.SaveChangesAsync();
        return Tokens(u, token);
    }
    public async Task<object> Refresh(RefreshInput input)
    {
        var hash = ValidateRefresh(input.RefreshToken);
        await using var tx = await db.Database.BeginTransactionAsync();
        // A row lock makes simultaneous refreshes deterministic. Reuse revokes the entire family.
        await LockFamily(hash);
        var s = await db.RefreshSessions.FromSqlInterpolated($"SELECT * FROM \"RefreshSessions\" WHERE \"TokenHash\" = {hash} FOR UPDATE").SingleOrDefaultAsync();
        Check.That(s != null, 401, "invalid_refresh", "登录会话无效");
        if (s!.RevokedAt != null)
        {
            await db.RefreshSessions.Where(x => x.FamilyId == s.FamilyId && x.RevokedAt == null)
                .ExecuteUpdateAsync(x => x.SetProperty(v => v.RevokedAt, DateTimeOffset.UtcNow));
            await tx.CommitAsync();
            throw new ApiError(401, "refresh_reused", "登录凭证已使用，请重新登录");
        }
        var u = await db.Users.FindAsync(s.UserId);
        Check.That(s.ExpiresAt > DateTimeOffset.UtcNow && u is { Disabled: false }, 401, "invalid_refresh", "登录会话已过期");
        var (next, token) = NewSession(s.UserId, s.FamilyId);
        s.RevokedAt = DateTimeOffset.UtcNow; s.ReplacedBy = next.Id;
        db.RefreshSessions.Add(next); await db.SaveChangesAsync(); await tx.CommitAsync();
        return Tokens(u!, token);
    }
    public async Task Logout(RefreshInput input)
    {
        var hash = ValidateRefresh(input.RefreshToken);
        // Serialize with refresh so logout cannot race a newly issued descendant.
        await using var tx = await db.Database.BeginTransactionAsync();
        await LockFamily(hash);
        var s = await db.RefreshSessions.FromSqlInterpolated($"SELECT * FROM \"RefreshSessions\" WHERE \"TokenHash\" = {hash} FOR UPDATE").SingleOrDefaultAsync();
        if (s != null) await db.RefreshSessions.Where(x => x.FamilyId == s.FamilyId && x.RevokedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(v => v.RevokedAt, DateTimeOffset.UtcNow));
        await tx.CommitAsync();
    }
    private async Task LockFamily(string hash)
    {
        var family = await db.RefreshSessions.AsNoTracking().Where(x => x.TokenHash == hash).Select(x => (Guid?)x.FamilyId).SingleOrDefaultAsync();
        if (family != null) {
            long key = BitConverter.ToInt64(family.Value.ToByteArray(), 0);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})");
        }
    }
    static string ValidateRefresh(string? t)
    { Check.That(t is { Length: 64 }, 401, "invalid_refresh", "登录会话无效"); return Hash(t!); }
    static (RefreshSession, string) NewSession(Guid id, Guid? family)
    {
        var t = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return (new RefreshSession { UserId = id, FamilyId = family ?? Guid.NewGuid(), TokenHash = Hash(t), ExpiresAt = DateTimeOffset.UtcNow.AddDays(14) }, t);
    }
    object Tokens(Player u, string refresh) => new { accessToken = keys.Access(u), expiresIn = 600, refreshToken = refresh, user = Profile(u) };
}
