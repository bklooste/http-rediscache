using System.ComponentModel.DataAnnotations;

namespace HttpRedisCache;

/// <summary>
/// The cache's tunable settings. Every default is applied here in code, so the service runs with no
/// configuration beyond <c>ConnectionStrings:Redis</c>. Every property must appear in the README config
/// table under its env var name (<c>Cache__PropertyName</c>) — <c>ReadmeConfigTableTests</c> enforces it.
/// </summary>
public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>Seconds a response is served as <c>HIT</c> when neither the route (<c>ttl</c>) nor the upstream (<c>max-age</c>) says otherwise.</summary>
    [Range(1, 31_536_000)]
    public int DefaultTtlSeconds { get; set; } = 30;

    /// <summary>Seconds after expiry an entry may still be served as <c>STALE</c> while a GET refreshes it in the background. <c>0</c> disables stale-while-revalidate.</summary>
    [Range(0, 31_536_000)]
    public int StaleWhileRevalidateSeconds { get; set; } = 60;

    /// <summary>Timeout for a single upstream call. A timeout is answered with <c>502</c>.</summary>
    [Range(1, 3600)]
    public int UpstreamTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Prefix for every Redis key the service writes, so several environments can share one Redis.
    /// Changing it does not break anything — it just leaves the old entries to expire and starts cold.
    /// </summary>
    [Required, RegularExpression(@"^[A-Za-z0-9:_-]+$")]
    public string KeyPrefix { get; set; } = "plat-cache:";
}
