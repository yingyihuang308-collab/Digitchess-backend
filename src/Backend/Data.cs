using Microsoft.EntityFrameworkCore;

namespace Backend;

public sealed class Player
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string LoginName { get; set; } = "";
    public string NormalizedLogin { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Nickname { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Disabled { get; set; }
}
public sealed class RefreshSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Player User { get; set; } = null!;
    public Guid FamilyId { get; set; } = Guid.NewGuid();
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedBy { get; set; }
}
public sealed class GameDb(DbContextOptions<GameDb> options) : DbContext(options)
{
    public DbSet<Player> Users => Set<Player>();
    public DbSet<RefreshSession> RefreshSessions => Set<RefreshSession>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Player>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.NormalizedLogin).IsUnique();
            e.Property(x => x.LoginName).HasMaxLength(32);
            e.Property(x => x.NormalizedLogin).HasMaxLength(32);
            e.Property(x => x.Nickname).HasMaxLength(32);
        });
        b.Entity<RefreshSession>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
public sealed class ApiError(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
public static class Check
{
    public static void That(bool condition, int status, string code, string message)
    { if (!condition) throw new ApiError(status, code, message); }
    public static string Text(string? value, int min, int max, string field)
    {
        var s = value?.Trim() ?? "";
        That(s.Length >= min && s.Length <= max && !s.Any(char.IsControl) && !s.Contains('<') && !s.Contains('>'), 400, "invalid_input", $"{field}长度须为{min}至{max}且不包含控制字符或尖括号");
        return s;
    }
}
