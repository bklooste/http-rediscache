using System.Net;
using HttpRedisCache;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;

// Container HEALTHCHECK: the chiselled runtime image has no shell or curl, so the app probes itself.
if (args.Contains("--healthcheck"))
{
    var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';')[0] ?? "8080";
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try { return (await http.GetAsync($"http://localhost:{port}/health")).IsSuccessStatusCode ? 0 : 1; }
    catch { return 1; }
}

var builder = WebApplication.CreateBuilder(args);
// Environment-wide defaults (GLOBAL_ConnectionStrings__Redis, from a shared env file) sit below appsettings.json.
builder.Configuration.AddGlobalDefaults();
// Also read DOTNET_-prefixed env vars (DOTNET_ConnectionStrings__Redis), and let them win over the plain
// form — existing deployments configure the service that way. Then apply *_FILE secret mounts.
builder.Configuration.AddEnvironmentVariables("DOTNET_");
builder.Configuration.AddSecretFiles();

builder.Services.AddOptions<CacheOptions>()
    .BindConfiguration(CacheOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Redis is the one required setting. Resolved lazily from config so it is validated where the rest of the
// host is built; resolved eagerly below so a bad or missing value fails the container at startup.
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var connectionString = sp.GetRequiredService<IConfiguration>()["ConnectionStrings:Redis"];
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException(
            "ConnectionStrings:Redis is required (env var ConnectionStrings__Redis, e.g. 'redis:6379,abortConnect=false').");
    return ConnectionMultiplexer.Connect(connectionString);
});
builder.Services.AddSingleton<CacheService>();

builder.Services.AddHttpClient(CacheEndpoints.UpstreamHttpClientName, (sp, client) =>
        client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<CacheOptions>>().Value.UpstreamTimeoutSeconds))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2), // re-resolve DNS for upstreams behind a service name
        // Always cache identity bytes: the response is stored and replayed without its Content-Encoding header,
        // so a compressed body would reach clients undecodable. The client's Accept-Encoding is not forwarded.
        AutomaticDecompression = DecompressionMethods.All,
    });

builder.Services.AddHealthChecks()
    .AddRedis(sp => sp.GetRequiredService<IConnectionMultiplexer>(), name: "redis");

// Telemetry: exporters are driven entirely by the standard OTEL_* env vars (OTEL_EXPORTER_OTLP_ENDPOINT,
// OTEL_SERVICE_NAME, ...). With none set, nothing is exported.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(builder.Environment.ApplicationName))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRedisInstrumentation(o => o.EnrichActivityWithTimingEvents = false) // instruments the IConnectionMultiplexer from DI
        .AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter());

var app = builder.Build();
try
{
    _ = app.Services.GetRequiredService<IConnectionMultiplexer>();
}
catch (Exception ex)
{
    // Exit cleanly with the message. Left unhandled, the exception aborts the runtime, which in the chiselled
    // image dies with SIGSEGV (exit 139) and buries the cause under a stack trace.
    Console.Error.WriteLine($"[http-rediscache] Startup failed: {ex.Message}");
    return 1;
}

app.MapHealthChecks("/health");
app.AddCacheApis();

app.Run();
return 0;

public partial class Program;
