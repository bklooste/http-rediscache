using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace HttpRedisCache.Tests.Support;

/// <summary>The cache service hosted in-process, configured the way a container would be: settings only.</summary>
public sealed class CacheApp(Dictionary<string, string?> settings) : WebApplicationFactory<Program>
{
    /// <summary>App wired to <paramref name="redis"/> with a private key prefix so tests never see each other's entries.</summary>
    public static CacheApp For(RedisFixture redis, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = redis.ConnectionString,
            ["Cache:KeyPrefix"] = $"t-{Guid.NewGuid():N}:",
        };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        return new CacheApp(settings);
    }

    public string KeyPrefix => settings["Cache:KeyPrefix"]!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in settings)
            if (value is not null) builder.UseSetting(key, value);
    }
}
