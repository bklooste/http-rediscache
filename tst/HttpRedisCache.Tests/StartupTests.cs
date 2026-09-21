using System.Net;
using HttpRedisCache.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace HttpRedisCache.Tests;

/// <summary>The container's config surface: defaults, fail-fast validation, and the env-var conventions.</summary>
[Collection("redis")]
public class StartupTests(RedisFixture redis)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CacheApp OnlyRedis(string connectionString) => new(new() { ["ConnectionStrings:Redis"] = connectionString });

    [Fact]
    public async Task Starts_with_nothing_but_a_redis_connection_string_and_applies_every_documented_default()
    {
        await using var app = OnlyRedis(redis.ConnectionString);
        using var client = app.CreateClient();

        var options = app.Services.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.Equal(30, options.DefaultTtlSeconds);
        Assert.Equal(60, options.StaleWhileRevalidateSeconds);
        Assert.Equal(10, options.UpstreamTimeoutSeconds);
        Assert.Equal("plat-cache:", options.KeyPrefix);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
    }

    [Fact]
    public async Task With_only_a_redis_connection_string_the_cache_still_caches()
    {
        await using var upstream = await Upstream.StartAsync();
        await using var app = OnlyRedis(redis.ConnectionString);
        using var client = app.CreateClient();
        var path = "/only-redis-" + Guid.NewGuid().ToString("N"); // default prefix is shared, so keep the URL unique
        var url = $"/cache?url={Uri.EscapeDataString(upstream.BaseUrl + path)}";

        var first = await client.GetAsync(url, Ct);
        var second = await client.GetAsync(url, Ct);

        Assert.Equal("MISS", first.Headers.GetValues("X-Cache").Single());
        Assert.Equal("HIT", second.Headers.GetValues("X-Cache").Single());
    }

    [Fact]
    public async Task Redis_calls_are_traced()
    {
        var sources = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = s => s.Name.Contains("Redis", StringComparison.Ordinal), // "OpenTelemetry.Redis" in the current package
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStopped = a => sources.Add(a.OperationName),
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        await using var upstream = await Upstream.StartAsync();
        await using var app = CacheApp.For(redis).WithWebHostBuilder(b => b.ConfigureServices(services =>
            // The instrumentation buffers Redis commands and turns them into spans every FlushInterval (10s by default).
            services.Configure<OpenTelemetry.Instrumentation.StackExchangeRedis.StackExchangeRedisInstrumentationOptions>(
                o => o.FlushInterval = TimeSpan.FromMilliseconds(100))));
        using var client = app.CreateClient();
        await client.GetAsync($"/cache?url={Uri.EscapeDataString(upstream.BaseUrl + "/traced")}", Ct);

        for (var i = 0; i < 100 && sources.IsEmpty; i++) await Task.Delay(50, Ct);
        Assert.NotEmpty(sources);
    }

    [Fact]
    public void A_missing_redis_connection_string_fails_startup_and_names_the_setting()
    {
        using var app = new CacheApp(new());

        var ex = Assert.ThrowsAny<Exception>(() => app.CreateClient());

        Assert.Contains("ConnectionStrings:Redis", ex.ToString());
    }

    [Theory]
    [InlineData("Cache:KeyPrefix", "has spaces!")]
    [InlineData("Cache:KeyPrefix", "")]
    [InlineData("Cache:DefaultTtlSeconds", "0")]
    [InlineData("Cache:StaleWhileRevalidateSeconds", "-1")]
    [InlineData("Cache:UpstreamTimeoutSeconds", "0")]
    public void Invalid_options_fail_fast_at_startup(string key, string value)
    {
        using var app = CacheApp.For(redis, new() { [key] = value });

        Assert.Throws<OptionsValidationException>(() => app.CreateClient());
    }

    [Fact]
    public async Task Redis_being_down_leaves_the_service_up_but_reports_unhealthy()
    {
        await using var app = OnlyRedis("127.0.0.1:1,abortConnect=false,connectTimeout=500,syncTimeout=500");
        using var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health", Ct)).StatusCode);
    }

    // ---- env var conventions (process-wide, hence the assembly is serial) ----

    [Fact]
    public void DOTNET_prefixed_env_vars_configure_the_service_for_backwards_compatibility()
    {
        using var _ = EnvVar.Set("DOTNET_Cache__KeyPrefix", "from-dotnet-env:");
        using var app = OnlyRedis(redis.ConnectionString);
        app.CreateClient().Dispose();

        Assert.Equal("from-dotnet-env:", app.Services.GetRequiredService<IOptions<CacheOptions>>().Value.KeyPrefix);
    }

    [Fact]
    public void DOTNET_prefixed_env_var_supplies_the_redis_connection_string()
    {
        using var _ = EnvVar.Set("DOTNET_ConnectionStrings__Redis", redis.ConnectionString);
        using var app = new CacheApp(new());

        app.CreateClient().Dispose(); // would throw if the connection string were not picked up
    }

    [Fact]
    public async Task A_FILE_env_var_supplies_a_secret_from_a_mounted_file()
    {
        var secret = Path.GetTempFileName();
        await File.WriteAllTextAsync(secret, redis.ConnectionString + "\n", Ct);
        try
        {
            using var _ = EnvVar.Set("ConnectionStrings__Redis_FILE", secret);
            await using var app = new CacheApp(new());
            using var client = app.CreateClient();

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
        }
        finally { File.Delete(secret); }
    }

    private sealed class EnvVar : IDisposable
    {
        private readonly string name;
        private EnvVar(string name) => this.name = name;

        public static EnvVar Set(string name, string value)
        {
            Environment.SetEnvironmentVariable(name, value);
            return new EnvVar(name);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(name, null);
    }
}
