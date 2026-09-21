using System.Text.Json;

using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace HttpRedisCache;

public static class CacheEndpoints
{
    public const string UpstreamHttpClientName = "http-rediscache-upstream";

    public static void AddCacheApis(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/cache", new[] { "GET", "POST", "PUT" }, HandleCacheRequest)
            .WithName("Cache")
            .WithTags("http-rediscache");
    }

    internal static async Task HandleCacheRequest(
        HttpContext context,
        CacheService cache,
        IHttpClientFactory httpClientFactory,
        IOptions<CacheOptions> options,
        ILogger<CacheService> logger,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        var query = request.Query;

        var rawUrl = query["url"].ToString();
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("missing 'url' query parameter", cancellationToken);
            return;
        }

        var method = HttpMethod.Parse(request.Method);
        var isGet = method == HttpMethod.Get;

        var cacheVarNames = query["cacheVar"].Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToArray();
        var cacheHeaderNames = query["cacheHeader"].Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToArray();

        TimeSpan? routeTtl = null;
        if (int.TryParse(query["ttl"].FirstOrDefault(), out var ttlSec) && ttlSec > 0)
            routeTtl = TimeSpan.FromSeconds(ttlSec);

        var (upstreamUri, upstreamPath, upstreamQuery) = BuildUpstreamUri(rawUrl, query);

        // Buffer body for POST/PUT so we can both extract cache-key fields and forward upstream.
        byte[]? requestBody = null;
        Dictionary<string, string>? bodyFields = null;
        var requestContentType = request.ContentType;
        if (!isGet)
        {
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms, cancellationToken);
            requestBody = ms.ToArray();
            bodyFields = TryParseJsonBodyFields(requestBody, requestContentType);
        }

        var cacheVars = cacheVarNames
            .Select(n =>
            {
                if (upstreamQuery.TryGetValue(n, out var qv))
                    return new KeyValuePair<string, string>(n, string.Join(',', qv.ToArray()));
                if (bodyFields != null && bodyFields.TryGetValue(n, out var bv))
                    return new KeyValuePair<string, string>(n, bv);
                return new KeyValuePair<string, string>(n, string.Empty);
            })
            .ToArray();
        var cacheHeaders = cacheHeaderNames
            .Select(n => new KeyValuePair<string, string>(n, request.Headers.TryGetValue(n, out var v) ? string.Join(',', v.ToArray()) : string.Empty))
            .ToArray();

        var settings = options.Value;
        var cacheKey = CacheService.BuildKey(settings.KeyPrefix, method.Method + " " + upstreamPath, cacheVars, cacheHeaders);

        var defaultTtl = TimeSpan.FromSeconds(settings.DefaultTtlSeconds);
        var staleWhileRevalidate = TimeSpan.FromSeconds(settings.StaleWhileRevalidateSeconds);

        var bodyCacheable = isGet || bodyFields != null;
        var clientWantsFresh = CacheService.ClientWantsFresh(request.Headers);

        if (!clientWantsFresh && bodyCacheable)
        {
            using var existing = await cache.GetAsync(cacheKey);
            if (existing != null)
            {
                var age = DateTimeOffset.UtcNow - existing.StoredAt;
                if (age < existing.Ttl)
                {
                    await WriteCachedResponse(context, existing, age, "HIT", cancellationToken);
                    return;
                }

                if (age < existing.Ttl + staleWhileRevalidate)
                {
                    if (isGet)
                    {
                        var headersSnapshot = SnapshotForwardableHeaders(request.Headers);
                        _ = Task.Run(() => RefreshAsync(httpClientFactory, cache, upstreamUri, cacheKey, headersSnapshot, routeTtl, defaultTtl, logger, CancellationToken.None));
                    }
                    // SWR is GET-only — POST/PUT serve stale without background refresh per design.
                    await WriteCachedResponse(context, existing, age, "STALE", cancellationToken);
                    return;
                }
            }
        }

        var client = httpClientFactory.CreateClient(UpstreamHttpClientName);
        using var upstreamRequest = BuildUpstreamRequest(method, upstreamUri, request.Headers, requestBody, requestContentType);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Upstream call failed {Method} {Url}", method, upstreamUri);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsync("upstream unavailable", cancellationToken);
            return;
        }

        try
        {
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

            if (response.IsSuccessStatusCode && bodyCacheable)
            {
                var ttl = CacheService.ResolveTtl(response, routeTtl, defaultTtl);
                if (ttl.HasValue)
                    await cache.SetAsync(cacheKey, body, contentType, ttl.Value);
            }

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = contentType;
            SetCacheHeaders(context.Response, "MISS", TimeSpan.Zero);
            await context.Response.Body.WriteAsync(body, cancellationToken);
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Best-effort parse of a JSON body to a flat string-keyed map for cache-key extraction.
    /// Returns null if the content type is not JSON or the body fails to parse — caller treats null as "not cacheable".
    /// </summary>
    public static Dictionary<string, string>? TryParseJsonBodyFields(byte[] body, string? contentType)
    {
        if (body.Length == 0)
            return null;
        if (contentType == null || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    result[prop.Name] = prop.Value.ToString();
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteCachedResponse(HttpContext context, CacheEntry entry, TimeSpan age, string state, CancellationToken ct)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = entry.ContentType;
        SetCacheHeaders(context.Response, state, age);
        await context.Response.Body.WriteAsync(entry.Body, ct);
    }


    private static void SetCacheHeaders(HttpResponse response, string cacheState, TimeSpan age)
    {
        response.Headers["X-Cache"] = cacheState;
        response.Headers["Age"] = ((long)Math.Max(0, age.TotalSeconds)).ToString();
    }

    private static (Uri Uri, string PathBase, IDictionary<string, StringValues> Query) BuildUpstreamUri(string rawUrl, IQueryCollection clientQuery)
    {
        var targetUrl = rawUrl;
        if (targetUrl.IndexOf("://", StringComparison.Ordinal) < 0)
            targetUrl = "http://" + targetUrl;

        var uri = new Uri(targetUrl, UriKind.Absolute);

        var qb = new QueryBuilder();
        foreach (var (k, v) in QueryHelpers.ParseQuery(uri.Query))
            foreach (var item in v)
                if (item != null)
                    qb.Add(k, item);

        foreach (var (k, v) in clientQuery)
        {
            if (k is "url" or "cacheVar" or "cacheHeader" or "ttl")
                continue;
            foreach (var item in v)
                if (item != null)
                    qb.Add(k, item);
        }

        var rebuilt = new UriBuilder(uri) { Query = qb.ToQueryString().Value?.TrimStart('?') ?? string.Empty };
        var path = uri.GetLeftPart(UriPartial.Path);
        var combinedQuery = QueryHelpers.ParseQuery(rebuilt.Query);

        return (rebuilt.Uri, path, combinedQuery);
    }

    private static Dictionary<string, string[]> SnapshotForwardableHeaders(IHeaderDictionary headers)
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in headers)
        {
            if (HopByHop.Contains(name) || string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase))
                continue;
            result[name] = values.ToArray()!;
        }
        return result;
    }

    private static HttpRequestMessage BuildUpstreamRequest(HttpMethod method, Uri uri, IHeaderDictionary clientHeaders, byte[]? body, string? contentType)
    {
        var msg = new HttpRequestMessage(method, uri);
        foreach (var (name, values) in clientHeaders)
        {
            if (HopByHop.Contains(name) || string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase))
                continue;
            msg.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
        }
        if (body is { Length: > 0 } && method != HttpMethod.Get)
        {
            var content = new ByteArrayContent(body);
            if (!string.IsNullOrEmpty(contentType))
                content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            msg.Content = content;
        }
        return msg;
    }

    private static HttpRequestMessage BuildUpstreamRequest(Uri uri, IReadOnlyDictionary<string, string[]> headers)
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var (name, values) in headers)
        {
            msg.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
        }
        return msg;
    }

    private static async Task RefreshAsync(
        IHttpClientFactory httpClientFactory,
        CacheService cache,
        Uri upstreamUri,
        string cacheKey,
        IReadOnlyDictionary<string, string[]> headers,
        TimeSpan? routeTtl,
        TimeSpan defaultTtl,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(UpstreamHttpClientName);
            using var req = BuildUpstreamRequest(upstreamUri, headers);
            using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return;

            var ttl = CacheService.ResolveTtl(response, routeTtl, defaultTtl);
            if (!ttl.HasValue)
                return;

            var body = await response.Content.ReadAsByteArrayAsync(ct);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            await cache.SetAsync(cacheKey, body, contentType, ttl.Value);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Background cache refresh failed {Url}", upstreamUri);
        }
    }

    // Request headers never forwarded upstream: hop-by-hop headers, the ones rebuilt from the buffered body,
    // and Accept-Encoding — the upstream client negotiates its own so cached bodies are always identity.
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Proxy-Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Upgrade",
        "Content-Length", "Content-Type", "Accept-Encoding",
    };
}
