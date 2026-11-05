using System.Net;
using Microsoft.EntityFrameworkCore;
using OneArr.Api;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure;
using OneArr.Infrastructure.Http;
using OneArr.Infrastructure.Legacy;
using OneArr.Infrastructure.Persistence;
using OneArr.Infrastructure.Security;
using OneArr.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

var configDir = Environment.GetEnvironmentVariable("ONEARR_CONFIG_DIR") ?? Path.Combine(Directory.GetCurrentDirectory(), "config");
Directory.CreateDirectory(configDir);
// Refuse a second backend on the same configuration before touching the database.
if (!OneArr.Api.InstanceLock.TryAcquire(configDir))
{
    Console.Error.WriteLine("Another OneArr instance is already using this configuration folder. Not starting.");
    return 3;
}
// A stop request left by an earlier tray session is stale: this process is starting, not stopping.
try { File.Delete(Path.Combine(configDir, "stop.request")); } catch (IOException) { }
builder.Services.AddHostedService(sp => new OneArr.Api.StopRequestWatcher(configDir, sp.GetRequiredService<IHostApplicationLifetime>()));
var dbPath = Path.Combine(configDir, "onearr.db");
var persistentSecretKey = TokenService.LoadOrCreateKey(Path.Combine(configDir, "secret.key"));
var tokens = new TokenService(persistentSecretKey);
var port = Environment.GetEnvironmentVariable("ONEARR_PORT") ?? "8780";
// Loopback unless told otherwise. The container image sets ONEARR_LISTEN_ADDRESS=0.0.0.0 and publishes via compose.
var listenAddress = Environment.GetEnvironmentVariable("ONEARR_LISTEN_ADDRESS") ?? "127.0.0.1";
builder.WebHost.UseUrls($"http://{listenAddress}:{port}");

var tmdbBase = Environment.GetEnvironmentVariable("ONEARR_TMDB_BASE_URL");
builder.Services.AddOneArrInfrastructure(dbPath, Environment.GetEnvironmentVariable("ONEARR_TMDB_API_KEY"),
    string.IsNullOrWhiteSpace(tmdbBase) ? null : UrlGuard.RequireServiceUrl(tmdbBase));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(tokens);
builder.Services.AddSingleton(new TrackerSecretBox(persistentSecretKey));
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();
app.Lifetime.ApplicationStopped.Register(OneArr.Api.InstanceLock.Release);

// Schema is created by EF Core migrations at startup; the database file lives in /config and survives restarts.
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<OneArrDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
    if (!await db.QualityProfiles.AnyAsync())
    {
        db.QualityProfiles.Add(new QualityProfile { Name = "HD-1080p", MinResolution = 720, CutoffResolution = 1080, AllowWebSources = true });
        db.QualityProfiles.Add(new QualityProfile { Name = "UHD-2160p", MinResolution = 1080, CutoffResolution = 2160, AllowWebSources = true });
        await db.SaveChangesAsync();
    }
}

app.UseDefaultFiles();
app.UseStaticFiles();

// Every /api route except auth and health needs a valid bearer token. The browser never sees service credentials.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase))
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..] : "";
        if (token.Length == 0 || !tokens.Validate(token, DateTimeOffset.UtcNow))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "Sign in required" });
            return;
        }
    }
    await next();
});

// Map domain exceptions to safe, redacted responses.
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (ProviderNotConfiguredException ex)
    {
        await WriteError(ctx, 409, ex.Message);
    }
    catch (ExternalServiceException ex)
    {
        await WriteError(ctx, 502, OneArr.Infrastructure.Http.OutboundHttp.Redact(ex.Message));
    }
    catch (ArgumentException ex)
    {
        await WriteError(ctx, 400, ex.Message);
    }
    catch (UnauthorizedAccessException ex)
    {
        await WriteError(ctx, 400, ex.Message);
    }
});

app.MapGet("/health", async (IDbContextFactory<OneArrDbContext> db) =>
{
    await using var ctx = await db.CreateDbContextAsync();
    var dbOk = await ctx.Database.CanConnectAsync();
    return Results.Ok(new { status = dbOk ? "ok" : "degraded", database = dbOk ? "ok" : "unreachable", version = BuildVersion });
});

ApiEndpoints.Map(app);

app.MapFallbackToFile("index.html");

app.Run();
return 0;

static async Task WriteError(HttpContext ctx, int status, string message)
{
    if (ctx.Response.HasStarted) return;
    ctx.Response.StatusCode = status;
    await ctx.Response.WriteAsJsonAsync(new { error = message });
}

// Exposes the entry point to WebApplicationFactory in tests.
// Exposes the entry point to WebApplicationFactory in tests.
public partial class Program
{
    /// <summary>Build version from the assembly, e.g. 0.2.0. Shown by /health so the tray can confirm an update took effect.</summary>
    public static string BuildVersion => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
