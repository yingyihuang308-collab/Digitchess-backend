using System.IdentityModel.Tokens.Jwt;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Backend;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder=WebApplication.CreateBuilder(new WebApplicationOptions{Args=args,ContentRootPath=AppContext.BaseDirectory});
var database=builder.Configuration.GetConnectionString("Database");
if(string.IsNullOrWhiteSpace(database))throw new InvalidOperationException("Configure ConnectionStrings__Database.");
builder.Services.AddDbContext<GameDb>(o=>o.UseNpgsql(database));
builder.Services.AddSingleton<AuthKeys>();
builder.Services.AddScoped<Accounts>();
builder.Services.AddSingleton<PasswordHasher<Player>>();
builder.Services.AddSingleton<Rooms>();
builder.Services.AddSingleton<Realtime>();
builder.Services.AddHostedService<RoomCleanup>();
builder.Services.AddHostedService<RealtimeCleanup>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<AuthKeys>((o,keys)=>{
    o.MapInboundClaims=false;o.TokenValidationParameters=keys.Validation;
    o.Events=new JwtBearerEvents {
        OnChallenge=async c=>{c.HandleResponse();c.Response.StatusCode=401;await c.Response.WriteAsJsonAsync(new {code="unauthorized",message="请先登录"});},
        OnTokenValidated=async c=>{
            if(!Guid.TryParse(c.Principal?.FindFirstValue("sub"),out var id) || !await c.HttpContext.RequestServices.GetRequiredService<GameDb>().Users.AnyAsync(x=>x.Id==id && !x.Disabled))c.Fail("Inactive user");
        }
    };
});
builder.Services.AddAuthorization();
var origins=builder.Configuration.GetSection("Cors:Origins").Get<string[]>()??[];
builder.Services.AddCors(o=>o.AddDefaultPolicy(p=>{if(origins.Length>0)p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();}));
var proxy=builder.Configuration["Proxy:Address"];
if(!string.IsNullOrEmpty(proxy))builder.Services.Configure<ForwardedHeadersOptions>(o=>{
    o.ForwardedHeaders=ForwardedHeaders.XForwardedFor|ForwardedHeaders.XForwardedProto;
    o.KnownProxies.Clear();o.KnownIPNetworks.Clear();o.KnownProxies.Add(IPAddress.Parse(proxy));o.ForwardLimit=1;
});
builder.Services.AddRateLimiter(o=>{
    o.RejectionStatusCode=429;
    o.OnRejected=async (c,t)=>{c.HttpContext.Response.StatusCode=429;await c.HttpContext.Response.WriteAsJsonAsync(new {code="rate_limited",message="请求过多，请稍后重试"},t);};
    o.GlobalLimiter=PartitionedRateLimiter.Create<HttpContext,string>(c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=180,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
    o.AddPolicy("auth",c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=20,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
    o.AddPolicy("ticket",c=>RateLimitPartition.GetFixedWindowLimiter(c.User.FindFirstValue("sub")??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=10,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
});
builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=128*1024);
// URLs may contain one-time websocket tickets. Never enable request-line logging.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics",LogLevel.Warning);
var app=builder.Build();
if(args.Contains("--migrate")) {
    await using var scope=app.Services.CreateAsyncScope();await scope.ServiceProvider.GetRequiredService<GameDb>().Database.MigrateAsync();return;
}
_ = app.Services.GetRequiredService<AuthKeys>();
app.Services.GetRequiredService<Realtime>().Initialize();
app.Use(async (c,next)=>{
    try {await next();}
    catch(ApiError e)when(!c.Response.HasStarted){c.Response.StatusCode=e.Status;await c.Response.WriteAsJsonAsync(new {code=e.Code,message=e.Message});}
    catch(Exception e)when(e is System.Text.Json.JsonException or BadHttpRequestException && !c.Response.HasStarted){c.Response.StatusCode=400;await c.Response.WriteAsJsonAsync(new {code="invalid_request",message="请求格式无效"});}
    catch(Exception e)when(!c.Response.HasStarted){app.Logger.LogError("Request failed type={Type} path={Path} trace={Trace}",e.GetType().Name,c.Request.Path.Value,c.TraceIdentifier);c.Response.StatusCode=500;await c.Response.WriteAsJsonAsync(new {code="internal_error",message="服务暂时不可用",traceId=c.TraceIdentifier});}
});
if(!string.IsNullOrEmpty(proxy))app.UseForwardedHeaders();
app.Use(async(c,next)=>{if(!app.Environment.IsDevelopment() && !c.Request.IsHttps && !c.Request.Path.StartsWithSegments("/health"))throw new ApiError(400,"https_required","请使用 HTTPS");await next();});
app.UseCors();
var wsOptions=new WebSocketOptions{KeepAliveInterval=TimeSpan.FromSeconds(20)};
foreach(var origin in origins)wsOptions.AllowedOrigins.Add(origin);
app.UseWebSockets(wsOptions);
app.UseAuthentication();app.UseRateLimiter();app.UseAuthorization();
app.MapGet("/health/live",()=>Results.Ok(new {status="alive"}));
app.MapGet("/health/ready",async(GameDb db)=>{try{await db.Users.AnyAsync();return Results.Ok(new {status="ready"});}catch(Exception){return Results.StatusCode(503);}});
app.MapPost("/api/auth/register",async(RegisterInput i,Accounts a)=>Results.Json(await a.Register(i),statusCode:201)).RequireRateLimiting("auth");
app.MapPost("/api/auth/login",async(LoginInput i,Accounts a)=>await a.Login(i)).RequireRateLimiting("auth");
app.MapPost("/api/auth/refresh",async(RefreshInput i,Accounts a)=>await a.Refresh(i)).RequireRateLimiting("auth");
app.MapPost("/api/auth/logout",async(RefreshInput i,Accounts a)=>{await a.Logout(i);return Results.NoContent();}).RequireRateLimiting("auth");
static Guid User(ClaimsPrincipal p)=>Guid.Parse(p.FindFirstValue("sub")!);
var api=app.MapGroup("/api").RequireAuthorization();
api.MapGet("/me",async(ClaimsPrincipal p,GameDb db)=>Accounts.Profile((await db.Users.FindAsync(User(p)))!));
api.MapPatch("/me",async(ClaimsPrincipal p,NicknameInput i,GameDb db)=>{var u=(await db.Users.FindAsync(User(p)))!;u.Nickname=Check.Text(i.Nickname,1,32,"昵称");await db.SaveChangesAsync();return Accounts.Profile(u);});
api.MapPost("/ws-ticket",(ClaimsPrincipal p,Realtime rt)=>rt.Ticket(User(p),DateTimeOffset.FromUnixTimeSeconds(long.Parse(p.FindFirstValue(JwtRegisteredClaimNames.Exp)!)))).RequireRateLimiting("ticket");
api.MapGet("/rooms",(int? offset,int? limit,string? search,Rooms r)=>r.List(offset??0,limit??10,search));
api.MapGet("/rooms/current",(ClaimsPrincipal p,Rooms r)=>Results.Json(r.Current(User(p))));
api.MapGet("/rooms/{id}",(ClaimsPrincipal p,string id,Rooms r)=>r.Get(User(p),id));
api.MapPost("/rooms",async(ClaimsPrincipal p,CreateRoomInput i,Rooms r,Realtime rt,GameDb db)=>{var nickname=(await db.Users.FindAsync(User(p)))!.Nickname;lock(r.Gate)return r.Create(User(p),nickname,i,rt.Online(User(p)));});
api.MapPost("/rooms/join",async(ClaimsPrincipal p,JoinInput i,Rooms r,Realtime rt,GameDb db)=>{var nickname=(await db.Users.FindAsync(User(p)))!.Nickname;lock(r.Gate)return r.Join(User(p),nickname,i,rt.Online(User(p)));});
api.MapPost("/rooms/{id}/ready",(ClaimsPrincipal p,string id,ReadyInput i,Rooms r)=>r.Ready(User(p),id,i.Ready));
api.MapPost("/rooms/{id}/metadata",(ClaimsPrincipal p,string id,MetadataInput i,Rooms r)=>r.Metadata(User(p),id,i.Metadata));
api.MapPost("/rooms/{id}/start",(ClaimsPrincipal p,string id,StartInput i,Rooms r)=>r.Start(User(p),id,i.Bootstrap));
api.MapPost("/rooms/{id}/return",(ClaimsPrincipal p,string id,Rooms r)=>r.Return(User(p),id));
api.MapPost("/rooms/{id}/leave",(ClaimsPrincipal p,string id,Rooms r)=>{r.Leave(User(p),id);return Results.NoContent();});
api.MapPost("/rooms/{id}/kick",(ClaimsPrincipal p,string id,KickInput i,Rooms r)=>{r.Kick(User(p),id,i.UserId);return Results.NoContent();});
api.MapPost("/rooms/{id}/close",(ClaimsPrincipal p,string id,Rooms r)=>{r.Close(User(p),id);return Results.NoContent();});
app.Map("/ws",async(HttpContext c,Realtime rt)=>await rt.Handle(c));
app.Run();
