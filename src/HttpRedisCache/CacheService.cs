using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

using StackExchange.Redis;

namespace HttpRedisCache;

public sealed class CacheEntry : IDisposable
{
    private IDisposable? lease;

    public required ReadOnlyMemory<byte> Body { get; init; }
    public required string ContentType { get; init; }
    public required DateTimeOffset StoredAt { get; init; }
    public required TimeSpan Ttl { get; init; }

    internal IDisposable? Lease
    {
        get => lease;
        init => lease = value;
    }

    public void Dispose()
    {
        lease?.Dispose();
        lease = null;
    }
}

public sealed class CacheService(IConnectionMultiplexer redis, ILogger<CacheService> logger)
{
    private readonly IDatabase db = redis.GetDatabase();
    private const string BodySuffix = ":b";
    private const string MetaSuffix = ":m";

    /// <summary>Redis key for a request: <c>{keyPrefix}{sha256(url|vars#headers)}</c>. The body and meta live at <c>:b</c> / <c>:m</c> beneath it.</summary>
    public static string BuildKey(string keyPrefix, string urlBase, IReadOnlyList<KeyValuePair<string, string>> cacheVars, IReadOnlyList<KeyValuePair<string, string>> cacheHeaders)
    {
        var sb = new StringBuilder(urlBase);
        foreach (var (k, v) in cacheVars.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append('|').Append(k).Append('=').Append(v);
        foreach (var (k, v) in cacheHeaders.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append('#').Append(k).Append('=').Append(v);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return keyPrefix + Convert.ToHexString(hash);
    }

    public async Task<CacheEntry?> GetAsync(string key)
    {
        var bodyTask = db.StringGetLeaseAsync(key + BodySuffix);
        var metaTask = db.StringGetAsync(key + MetaSuffix);
        await Task.WhenAll(bodyTask, metaTask);

        var bodyLease = bodyTask.Result;
        if (bodyLease is null)
            return null;

        var meta = metaTask.Result;
        if (!meta.HasValue || !TryParseMeta(meta!, out var contentType, out var storedAt, out var ttl))
        {
            bodyLease.Dispose();
            return null;
        }

        return new CacheEntry
        {
            Body = bodyLease.Memory,
            Lease = bodyLease,
            ContentType = contentType,
            StoredAt = storedAt,
            Ttl = ttl,
        };
    }

    public async Task SetAsync(string key, byte[] body, string contentType, TimeSpan ttl)
    {
        var now = DateTimeOffset.UtcNow;
        var bodyKey = key + BodySuffix;
        var metaKey = key + MetaSuffix;

        // Use a slightly longer expiry than ttl so stale-while-revalidate can still hit the entry briefly after expiry.
        var redisExpiry = ttl + TimeSpan.FromMinutes(5);

        var meta = FormatMeta(contentType, now, ttl);

        var setBodyTask = db.StringSetAsync(bodyKey, body, redisExpiry);
        var setMetaTask = db.StringSetAsync(metaKey, meta, redisExpiry);

        try
        {
            await Task.WhenAll(setBodyTask, setMetaTask);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write cache entry {Key}", key);
        }
    }

    // Meta format: "{storedAtTicks}|{ttlSeconds}|{contentType}"
    internal static string FormatMeta(string contentType, DateTimeOffset storedAt, TimeSpan ttl)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return storedAt.UtcTicks.ToString(inv) + "|" + ((long)ttl.TotalSeconds).ToString(inv) + "|" + contentType;
    }

    internal static bool TryParseMeta(string raw, out string contentType, out DateTimeOffset storedAt, out TimeSpan ttl)
    {
        contentType = string.Empty;
        storedAt = default;
        ttl = default;

        var first = raw.IndexOf('|');
        if (first <= 0)
            return false;
        var second = raw.IndexOf('|', first + 1);
        if (second <= first + 1 || second == raw.Length - 1)
            return false;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!long.TryParse(raw.AsSpan(0, first), System.Globalization.NumberStyles.Integer, inv, out var ticks))
            return false;
        if (!long.TryParse(raw.AsSpan(first + 1, second - first - 1), System.Globalization.NumberStyles.Integer, inv, out var ttlSeconds))
            return false;

        contentType = raw[(second + 1)..];
        storedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        ttl = TimeSpan.FromSeconds(ttlSeconds);
        return true;
    }

    /// <summary>
    /// Determine the TTL for a cached upstream response.
    /// - Upstream no-store/no-cache/private directives always win — they signal "do not cache" and
    ///   must not be overridden by a route TTL (the upstream is explicitly opting out of caching).
    /// - When routeTtl is provided and upstream permits caching, routeTtl takes precedence over
    ///   upstream Cache-Control max-age/s-maxage.
    /// - Falls back to upstream max-age/s-maxage, then defaultTtl.
    /// </summary>
    public static TimeSpan? ResolveTtl(HttpResponseMessage response, TimeSpan? routeTtl, TimeSpan defaultTtl)
    {
        var cacheControl = response.Headers.CacheControl;
        if (cacheControl != null)
        {
            // Upstream explicitly opts out of caching — honour this regardless of routeTtl.
            if (cacheControl.NoStore || cacheControl.NoCache || cacheControl.Private)
                return null;

            if (routeTtl.HasValue)
                return routeTtl.Value;

            var maxAge = cacheControl.SharedMaxAge ?? cacheControl.MaxAge;
            if (maxAge.HasValue)
                return maxAge.Value <= TimeSpan.Zero ? null : maxAge.Value;
        }

        return routeTtl ?? defaultTtl;
    }

    public static bool ClientWantsFresh(IHeaderDictionary headers)
    {
        if (!headers.TryGetValue("Cache-Control", out var values))
            return false;

        foreach (var v in values)
        {
            if (string.IsNullOrEmpty(v))
                continue;
            if (CacheControlHeaderValue.TryParse(v, out var cc) && (cc.NoCache || cc.NoStore))
                return true;
        }
        return false;
    }
}
