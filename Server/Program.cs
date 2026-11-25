using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using SecureVideo;
using SecureVideo.Server;

var vault = new Vault();
bool localPlayer = args is ["local-player", _, _];
const int defaultPort = 5185;
int ownerId = 0;
int localPort = defaultPort;
bool localService = args.Length is 2 or 3 && args[0] == "serve-local" &&
    int.TryParse(args[1], out ownerId) && ownerId > 0 &&
    (args.Length == 2 || int.TryParse(args[2], out localPort) && localPort is > 0 and <= 65535);
if (args.Length > 0 && args[0] != "serve" && !localPlayer && !localService)
{
    try { Admin.Run(vault, args); }
    catch (Exception error) { Console.Error.WriteLine(error.Message); Environment.ExitCode = 1; }
    return;
}

string? playerPath = null;
if (localPlayer)
{
    playerPath = Path.GetFullPath(args[1]);
    var configuration = Path.GetFullPath(args[2]);
    if (!File.Exists(playerPath)) throw new FileNotFoundException("请先构建播放器。", playerPath);
    if (!File.Exists(configuration))
    {
        vault.Initialize();
        Console.Write("初始管理员账号：");
        var account = Console.ReadLine() ?? "";
        Admin.Run(vault, ["add-admin", account, "3650", "3"]);
        Directory.CreateDirectory(Path.GetDirectoryName(configuration)!);
        Admin.Run(vault, ["export-config", "http://127.0.0.1:5185", configuration]);
    }
    using var settings = JsonDocument.Parse(File.ReadAllBytes(configuration));
    var address = new Uri(settings.RootElement.GetProperty("Server").GetString()!);
    File.Copy(configuration, Path.Combine(Path.GetDirectoryName(playerPath)!, "player.json"), overwrite: true);
    if (!(address.IsLoopback && address.Scheme == "http"))
    {
        using var remotePlayer = Process.Start(new ProcessStartInfo(playerPath) { UseShellExecute = true });
        if (remotePlayer is not null) await remotePlayer.WaitForExitAsync();
        return;
    }
    localPort = address.Port;
}
vault.Use(_ => true);
var builder = WebApplication.CreateBuilder(localPlayer || localService ? [] : args.Skip(1).ToArray());
var allowLocalHttp = localPlayer || localService || Environment.GetEnvironmentVariable("SVIDEO_ALLOW_LOCAL_HTTP") == "1";
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
if (localPlayer || localService)
    builder.WebHost.UseUrls($"http://127.0.0.1:{localPort}");
else if (allowLocalHttp && string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls($"http://127.0.0.1:{defaultPort}");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress ?? IPAddress.None,
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("grant", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress ?? IPAddress.None,
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (!context.Request.IsHttps && !(allowLocalHttp &&
        context.Connection.LocalIpAddress is { } local && IPAddress.IsLoopback(local) &&
        context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote)))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseRateLimiter();
var authorizations = new AuthorizationService(vault, app.Logger);

app.MapPost("/api/login", (LoginRequest request) => authorizations.Login(request)).RequireRateLimiting("login");
app.MapPost("/api/challenge", (HttpContext context) => authorizations.Challenge(context)).RequireRateLimiting("grant");
app.MapPost("/api/grant", (HttpContext context, GrantRequest request) => authorizations.Grant(context, request))
    .RequireRateLimiting("grant");
app.MapPost("/api/admin", (HttpContext context, AdminRequest request) => authorizations.Manage(context, request))
    .RequireRateLimiting("grant");
if (localService)
{
    using var parent = Process.GetProcessById(ownerId);
    parent.EnableRaisingEvents = true;
    parent.Exited += (_, _) => app.Lifetime.StopApplication();
    if (!parent.HasExited) await app.RunAsync();
    return;
}
if (playerPath is null)
{
    await app.RunAsync();
    return;
}

await app.StartAsync();
try
{
    using var player = Process.Start(new ProcessStartInfo(playerPath) { UseShellExecute = true }) ??
        throw new InvalidOperationException("播放器启动失败。");
    await player.WaitForExitAsync(app.Lifetime.ApplicationStopping);
}
catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested) { }
finally
{
    await app.StopAsync();
    await app.DisposeAsync();
}
