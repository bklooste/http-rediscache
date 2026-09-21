using System.Net;
using System.Net.Sockets;
using System.Text;
using HttpRedisCache.Tests.Support;
using Microsoft.AspNetCore.Http;
using StackExchange.Redis;

namespace HttpRedisCache.Tests;

/// <summary>End to end: the real service, a real Redis (Testcontainers) and a real stub upstream.</summary>
[Collection("redis")]
public class CacheEndpointTests(RedisFixture redis) : IAsyncLifetime
{
    private Upstream upstream = null!;
    private CacheApp app = null!;
    private HttpClient client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        upstream = await Upstream.StartAsync();
        Use(CacheApp.For(redis));
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await app.DisposeAsync();
        await upstream.DisposeAsync();
    }

    private void Use(CacheApp next)
    {
        app = next;
        client = app.CreateClient();
    }

    /// <summary>Replace the app with one carrying extra settings (a fresh key prefix, so still isolated).</summary>
    private void Configure(Dictionary<string, string?> extra)
    {
        client.Dispose();
        app.Dispose();
        Use(CacheApp.For(redis, extra));
    }

    private string CacheUrl(string path, params (string Key, string Value)[] query)
    {
        var sb = new StringBuilder("/cache?url=").Append(Uri.EscapeDataString(upstream.BaseUrl + path));
        foreach (var (k, v) in query) sb.Append('&').Append(k).Append('=').Append(Uri.EscapeDataString(v));
        return sb.ToString();
    }

    private static string XCache(HttpResponseMessage r) => r.Headers.GetValues("X-Cache").Single();

    private async Task<(string State, string Body)> Get(string path, params (string, string)[] query)
    {
        var r = await client.GetAsync(CacheUrl(path, query), Ct);
        return (XCache(r), await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<bool> Eventually(Func<Task<bool>> condition, int seconds = 15)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (await condition()) return true;
            await Task.Delay(50, Ct);
        }
        return await condition();
    }

    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    // ---- MISS / HIT / STALE ----

    [Fact]
    public async Task First_call_is_a_MISS_and_the_second_is_a_HIT_without_touching_the_upstream()
    {
        var first = await client.GetAsync(CacheUrl("/thing"), Ct);
        var second = await client.GetAsync(CacheUrl("/thing"), Ct);

        Assert.Equal("MISS", XCache(first));
        Assert.Equal("0", first.Headers.GetValues("Age").Single());
        Assert.Equal("v1", await first.Content.ReadAsStringAsync(Ct));
        Assert.Equal("HIT", XCache(second));
        Assert.True(long.TryParse(second.Headers.GetValues("Age").Single(), out var age) && age >= 0);
        Assert.Equal("v1", await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, upstream.Calls);
    }

    [Fact]
    public async Task Content_type_is_preserved_through_the_cache()
    {
        upstream.Behavior = async (ctx, _) =>
        {
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync("""{"ok":true}""");
        };

        await client.GetAsync(CacheUrl("/j"), Ct);
        var hit = await client.GetAsync(CacheUrl("/j"), Ct);

        Assert.Equal("HIT", XCache(hit));
        Assert.Equal("application/json; charset=utf-8", hit.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task An_expired_entry_is_served_STALE_and_a_background_refresh_replaces_it()
    {
        Assert.Equal(("MISS", "v1"), await Get("/swr", ("ttl", "1")));

        // Poll until the 1s TTL lapses; the first non-HIT is the stale serve, still the old body.
        (string State, string Body) stale = default;
        Assert.True(await Eventually(async () => (stale = await Get("/swr", ("ttl", "1"))).State != "HIT"), "never left HIT");
        Assert.Equal(("STALE", "v1"), stale);

        // The background refresh calls the upstream again and the cache then serves a newer body. (Every STALE
        // hit refreshes, so later versions may already have landed by the time we look — "newer", not "v2".)
        (string State, string Body) latest = default;
        Assert.True(await Eventually(async () => (latest = await Get("/swr", ("ttl", "1"))).Body != "v1"),
            $"refresh never landed: last={latest}, upstream calls={upstream.Calls}");
        Assert.True(upstream.Calls >= 2);
    }

    [Fact]
    public async Task Past_the_stale_window_the_next_call_is_a_MISS_that_hits_the_upstream()
    {
        Configure(new() { ["Cache:StaleWhileRevalidateSeconds"] = "0" });
        Assert.Equal(("MISS", "v1"), await Get("/expire", ("ttl", "1")));

        (string State, string Body) next = default;
        Assert.True(await Eventually(async () => (next = await Get("/expire", ("ttl", "1"))).State != "HIT"));

        Assert.Equal(("MISS", "v2"), next);
        Assert.Equal(2, upstream.Calls);
    }

    [Fact]
    public async Task Stale_serving_for_POST_never_refreshes_in_the_background()
    {
        async Task<(string State, string Body)> Post()
        {
            var r = await client.PostAsync(CacheUrl("/post-swr", ("ttl", "1"), ("cacheVar", "k")), Json("""{"k":"1"}"""), Ct);
            return (XCache(r), await r.Content.ReadAsStringAsync(Ct));
        }

        Assert.Equal(("MISS", "v1"), await Post());
        (string State, string Body) stale = default;
        Assert.True(await Eventually(async () => (stale = await Post()).State != "HIT"));
        Assert.Equal(("STALE", "v1"), stale);

        await Task.Delay(300, Ct); // negative assertion: give a (wrongly) fired refresh time to reach the upstream
        Assert.Equal(1, upstream.Calls);
    }

    // ---- what is and is not cached ----

    [Fact]
    public async Task Upstream_errors_pass_through_and_are_never_cached()
    {
        upstream.Behavior = async (ctx, n) => { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync($"down{n}"); };

        var first = await client.GetAsync(CacheUrl("/err"), Ct);
        var second = await client.GetAsync(CacheUrl("/err"), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal("MISS", XCache(first));
        Assert.Equal("down2", await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal(2, upstream.Calls);
    }

    [Theory]
    [InlineData("no-store")]
    [InlineData("no-cache")]
    [InlineData("private, max-age=60")]
    public async Task Upstream_opt_out_directives_are_not_cached_even_with_a_route_ttl(string cacheControl)
    {
        upstream.Behavior = async (ctx, n) => { ctx.Response.Headers.CacheControl = cacheControl; await ctx.Response.WriteAsync($"v{n}"); };

        Assert.Equal("MISS", (await Get("/nocache", ("ttl", "300"))).State);
        Assert.Equal("MISS", (await Get("/nocache", ("ttl", "300"))).State);
        Assert.Equal(2, upstream.Calls);
    }

    [Fact]
    public async Task A_client_no_cache_request_bypasses_the_cache_and_refreshes_it()
    {
        await Get("/fresh");
        Assert.Equal("HIT", (await Get("/fresh")).State);

        using var req = new HttpRequestMessage(HttpMethod.Get, CacheUrl("/fresh"));
        req.Headers.CacheControl = new() { NoCache = true };
        var bypass = await client.SendAsync(req, Ct);
        Assert.Equal("MISS", XCache(bypass));
        Assert.Equal("v2", await bypass.Content.ReadAsStringAsync(Ct));

        Assert.Equal(("HIT", "v2"), await Get("/fresh"));
    }

    // ---- the cache key ----

    [Fact]
    public async Task Only_query_params_named_by_cacheVar_split_the_cache_the_rest_are_forwarded_but_share_an_entry()
    {
        // eventType is a cacheVar → separate entries. page is not → forwarded upstream but shares one entry.
        Assert.Equal(("MISS", "v1"), await Get("/nav", ("cacheVar", "eventType"), ("eventType", "Racing"), ("page", "1")));
        Assert.Equal(("MISS", "v2"), await Get("/nav", ("cacheVar", "eventType"), ("eventType", "Sport"), ("page", "1")));
        Assert.Equal(("HIT", "v1"), await Get("/nav", ("cacheVar", "eventType"), ("eventType", "Racing"), ("page", "2")));

        Assert.Contains(upstream.Requests, r => r.PathAndQuery == "/nav?eventType=Racing&page=1");
    }

    [Fact]
    public async Task Only_headers_named_by_cacheHeader_split_the_cache_and_all_headers_are_forwarded()
    {
        async Task<(string State, string Body)> As(string user, string trace)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, CacheUrl("/me", ("cacheHeader", "X-User")));
            req.Headers.Add("X-User", user);
            req.Headers.Add("X-Trace", trace);
            var r = await client.SendAsync(req, Ct);
            return (XCache(r), await r.Content.ReadAsStringAsync(Ct));
        }

        Assert.Equal(("MISS", "v1"), await As("alice", "t1"));
        Assert.Equal(("MISS", "v2"), await As("bob", "t1"));
        Assert.Equal(("HIT", "v1"), await As("alice", "t2")); // X-Trace is forwarded but not part of the key

        Assert.Equal("t1", upstream.Requests.First().Headers["X-Trace"]);
    }

    [Fact]
    public async Task Reserved_params_are_consumed_and_the_upstream_sees_its_own_host()
    {
        await client.GetAsync(CacheUrl("/fwd", ("cacheVar", "a"), ("cacheHeader", "X-H"), ("ttl", "5"), ("a", "1")), Ct);

        var seen = upstream.Requests.Single();
        Assert.Equal("/fwd?a=1", seen.PathAndQuery);
        Assert.Equal(new Uri(upstream.BaseUrl).Authority, seen.Headers["Host"]);
    }

    [Fact]
    public async Task A_scheme_less_url_is_called_over_http()
    {
        var host = new Uri(upstream.BaseUrl).Authority;
        var r = await client.GetAsync($"/cache?url={Uri.EscapeDataString(host + "/bare")}", Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("/bare", upstream.Requests.Single().PathAndQuery);
    }

    [Fact]
    public async Task A_missing_url_is_a_400()
    {
        var r = await client.GetAsync("/cache", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("url", await r.Content.ReadAsStringAsync(Ct));
    }

    // ---- POST / PUT bodies ----

    [Fact]
    public async Task A_JSON_body_is_forwarded_and_its_cacheVar_fields_key_the_entry()
    {
        async Task<(string State, string Body)> Post(string json)
        {
            var r = await client.PostAsync(CacheUrl("/search", ("cacheVar", "brand")), Json(json), Ct);
            return (XCache(r), await r.Content.ReadAsStringAsync(Ct));
        }

        Assert.Equal(("MISS", "v1"), await Post("""{"brand":"a","q":1}"""));
        Assert.Equal(("HIT", "v1"), await Post("""{"brand":"a","q":1}"""));
        Assert.Equal(("MISS", "v2"), await Post("""{"brand":"b","q":1}"""));
        // q is not a cacheVar, so a different q still shares brand a's entry.
        Assert.Equal(("HIT", "v1"), await Post("""{"brand":"a","q":999}"""));

        var forwarded = upstream.Requests.First();
        Assert.Equal("POST", forwarded.Method);
        Assert.Equal("""{"brand":"a","q":1}""", forwarded.Body);
        Assert.StartsWith("application/json", forwarded.Headers["Content-Type"]);
    }

    [Fact]
    public async Task PUT_is_cached_like_POST()
    {
        var url = CacheUrl("/put", ("cacheVar", "id"));
        await client.PutAsync(url, Json("""{"id":"1"}"""), Ct);
        var second = await client.PutAsync(url, Json("""{"id":"1"}"""), Ct);

        Assert.Equal("HIT", XCache(second));
        Assert.Equal("PUT", upstream.Requests.Single().Method);
    }

    [Fact]
    public async Task A_body_that_is_not_a_JSON_object_is_forwarded_but_never_cached()
    {
        async Task<string> Post(HttpContent body) => XCache(await client.PostAsync(CacheUrl("/raw"), body, Ct));

        Assert.Equal("MISS", await Post(new StringContent("hello", Encoding.UTF8, "text/plain")));
        Assert.Equal("MISS", await Post(new StringContent("hello", Encoding.UTF8, "text/plain")));
        Assert.Equal("MISS", await Post(Json("[1,2,3]")));
        Assert.Equal("MISS", await Post(Json("[1,2,3]")));

        Assert.Equal(4, upstream.Calls);
    }

    // ---- upstream failure ----

    [Fact]
    public async Task An_unreachable_upstream_is_a_502()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var r = await client.GetAsync($"/cache?url={Uri.EscapeDataString($"http://127.0.0.1:{closedPort}/x")}", Ct);

        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.Equal("upstream unavailable", await r.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task An_upstream_slower_than_the_timeout_is_a_502()
    {
        Configure(new() { ["Cache:UpstreamTimeoutSeconds"] = "1" });
        upstream.Behavior = async (ctx, _) => { await Task.Delay(TimeSpan.FromSeconds(4)); await ctx.Response.WriteAsync("late"); };

        var r = await client.GetAsync(CacheUrl("/slow"), Ct);

        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
    }

    // ---- encoding ----

    [Fact]
    public async Task A_compressing_upstream_is_cached_and_served_as_readable_content()
    {
        upstream.Behavior = async (ctx, _) =>
        {
            if (ctx.Request.Headers.AcceptEncoding.ToString().Contains("gzip"))
            {
                ctx.Response.Headers.ContentEncoding = "gzip";
                await using var gz = new System.IO.Compression.GZipStream(ctx.Response.Body, System.IO.Compression.CompressionLevel.Fastest);
                await gz.WriteAsync(Encoding.UTF8.GetBytes("hello compressed world"), Ct);
            }
            else
            {
                await ctx.Response.WriteAsync("hello compressed world");
            }
        };

        for (var i = 0; i < 2; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, CacheUrl("/gz"));
            req.Headers.AcceptEncoding.ParseAdd("gzip");
            var r = await client.SendAsync(req, Ct);

            Assert.Equal("hello compressed world", await r.Content.ReadAsStringAsync(Ct));
        }
    }

    // ---- what is stored in Redis ----

    [Fact]
    public async Task Entries_are_stored_under_the_key_prefix_and_expire_ttl_plus_five_minutes_after_the_route_ttl_wins()
    {
        upstream.Behavior = async (ctx, _) => { ctx.Response.Headers.CacheControl = "max-age=5"; await ctx.Response.WriteAsync("body"); };

        await Get("/layout", ("ttl", "100"));

        var keys = redis.KeysUnder(app.KeyPrefix).OrderBy(k => k).ToArray();
        Assert.Equal(2, keys.Length);
        Assert.Matches($"^{System.Text.RegularExpressions.Regex.Escape(app.KeyPrefix)}[0-9A-F]{{64}}:b$", keys[0]);
        Assert.Equal(keys[0][..^2] + ":m", keys[1]);

        var db = redis.Connection.GetDatabase();
        foreach (var key in keys)
        {
            var remaining = (await db.KeyTimeToLiveAsync(key))!.Value.TotalSeconds;
            Assert.InRange(remaining, 100 + 300 - 10, 100 + 300); // route ttl (100), not max-age (5), plus the 5 min SWR headroom
        }
    }
}
