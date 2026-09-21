using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace HttpRedisCache.Tests;

/// <summary>Pure logic: key building, TTL precedence, body parsing, meta format. No Redis needed.</summary>
public class CacheServiceTests
{
    private static readonly KeyValuePair<string, string>[] None = [];
    private static KeyValuePair<string, string> Kv(string k, string v) => new(k, v);

    // ---- key ----

    [Fact]
    public void Build_key_is_stable_for_same_inputs()
    {
        var vars = new[] { Kv("eventType", "Racing") };
        var headers = new[] { Kv("userId", "u1") };

        Assert.Equal(
            CacheService.BuildKey("p:", "svc/v1/nav", vars, headers),
            CacheService.BuildKey("p:", "svc/v1/nav", vars, headers));
    }

    [Fact]
    public void Build_key_ignores_cachevar_order()
    {
        var a = new[] { Kv("eventType", "Racing"), Kv("brand", "x") };
        var b = new[] { Kv("brand", "x"), Kv("eventType", "Racing") };

        Assert.Equal(CacheService.BuildKey("p:", "path", a, None), CacheService.BuildKey("p:", "path", b, None));
    }

    [Fact]
    public void Build_key_changes_when_var_value_changes()
    {
        Assert.NotEqual(
            CacheService.BuildKey("p:", "path", [Kv("eventType", "Racing")], None),
            CacheService.BuildKey("p:", "path", [Kv("eventType", "Sport")], None));
    }

    [Fact]
    public void Build_key_changes_when_path_changes()
    {
        Assert.NotEqual(CacheService.BuildKey("p:", "path/a", None, None), CacheService.BuildKey("p:", "path/b", None, None));
    }

    [Fact]
    public void Build_key_starts_with_the_prefix_and_ends_in_a_sha256_hex_digest()
    {
        var key = CacheService.BuildKey("my-env:", "path", None, None);

        Assert.StartsWith("my-env:", key);
        Assert.Matches("^[0-9A-F]{64}$", key["my-env:".Length..]);
    }

    [Fact]
    public void Build_key_differs_per_prefix_so_environments_can_share_a_redis()
    {
        Assert.NotEqual(CacheService.BuildKey("dev:", "path", None, None), CacheService.BuildKey("qa:", "path", None, None));
    }

    /// <summary>The default prefix must keep producing exactly the keys existing deployments already have.</summary>
    [Fact]
    public void Build_key_with_default_prefix_matches_the_historical_key_shape()
    {
        var key = CacheService.BuildKey(new CacheOptions().KeyPrefix, "GET http://svc/x", [Kv("a", "1")], [Kv("h", "2")]);

        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("GET http://svc/x|a=1#h=2")));
        Assert.Equal("plat-cache:" + expectedHash, key);
    }

    // ---- TTL precedence ----

    private static HttpResponseMessage Response(CacheControlHeaderValue? cc = null)
    {
        var r = new HttpResponseMessage();
        r.Headers.CacheControl = cc;
        return r;
    }

    [Fact]
    public void Resolve_ttl_uses_max_age_from_upstream()
    {
        using var r = Response(new() { MaxAge = TimeSpan.FromSeconds(120) });
        Assert.Equal(TimeSpan.FromSeconds(120), CacheService.ResolveTtl(r, null, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Resolve_ttl_falls_back_to_default_when_no_directive()
    {
        using var r = Response();
        Assert.Equal(TimeSpan.FromSeconds(45), CacheService.ResolveTtl(r, null, TimeSpan.FromSeconds(45)));
    }

    [Theory]
    [InlineData("no-store")]
    [InlineData("no-cache")]
    [InlineData("private")]
    public void Resolve_ttl_returns_null_when_upstream_opts_out(string directive)
    {
        using var r = Response(new()
        {
            NoStore = directive == "no-store",
            NoCache = directive == "no-cache",
            Private = directive == "private",
        });
        Assert.Null(CacheService.ResolveTtl(r, null, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Resolve_ttl_upstream_opt_out_wins_over_route_ttl()
    {
        using var r = Response(new() { NoStore = true });
        Assert.Null(CacheService.ResolveTtl(r, TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Resolve_ttl_prefers_s_maxage_over_max_age()
    {
        using var r = Response(new() { MaxAge = TimeSpan.FromSeconds(10), SharedMaxAge = TimeSpan.FromSeconds(60) });
        Assert.Equal(TimeSpan.FromSeconds(60), CacheService.ResolveTtl(r, null, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Resolve_ttl_route_ttl_beats_upstream_max_age()
    {
        using var r = Response(new() { SharedMaxAge = TimeSpan.FromSeconds(30) });
        Assert.Equal(TimeSpan.FromSeconds(300), CacheService.ResolveTtl(r, TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Resolve_ttl_without_route_ttl_uses_upstream_cache_control()
    {
        using var r = Response(new() { MaxAge = TimeSpan.FromSeconds(60) });
        Assert.Equal(TimeSpan.FromSeconds(60), CacheService.ResolveTtl(r, null, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Resolve_ttl_max_age_zero_is_not_cached()
    {
        using var r = Response(new() { MaxAge = TimeSpan.Zero });
        Assert.Null(CacheService.ResolveTtl(r, null, TimeSpan.FromSeconds(30)));
    }

    // ---- client Cache-Control ----

    [Theory]
    [InlineData("no-cache", true)]
    [InlineData("no-store", true)]
    [InlineData("max-age=600", false)]
    public void Client_wants_fresh_reads_cache_control(string value, bool expected)
    {
        var headers = new HeaderDictionary { ["Cache-Control"] = new StringValues(value) };
        Assert.Equal(expected, CacheService.ClientWantsFresh(headers));
    }

    [Fact]
    public void Client_wants_fresh_is_false_without_the_header()
    {
        Assert.False(CacheService.ClientWantsFresh(new HeaderDictionary()));
    }

    // ---- POST/PUT body → cache-key fields ----

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Parse_body_extracts_top_level_scalars_only()
    {
        var fields = CacheEndpoints.TryParseJsonBodyFields(Utf8("""{"brand":"x","page":3,"open":true,"nested":{"a":1}}"""), "application/json");

        Assert.NotNull(fields);
        Assert.Equal("x", fields["brand"]);
        Assert.Equal("3", fields["page"]);
        Assert.Equal("True", fields["open"]);
        Assert.DoesNotContain("nested", fields.Keys);
    }

    [Theory]
    [InlineData("plain", "text/plain")]
    [InlineData("", "application/json")]
    [InlineData("[1,2,3]", "application/json")]
    [InlineData("{not-json", "application/json")]
    public void Parse_body_returns_null_when_the_body_is_not_a_usable_json_object(string body, string contentType)
    {
        Assert.Null(CacheEndpoints.TryParseJsonBodyFields(Utf8(body), contentType));
    }

    [Fact]
    public void Parse_body_returns_null_without_a_content_type()
    {
        Assert.Null(CacheEndpoints.TryParseJsonBodyFields(Utf8("""{"a":1}"""), null));
    }

    // ---- stored meta ----

    [Fact]
    public void Meta_format_roundtrips()
    {
        var stored = new DateTimeOffset(2026, 5, 4, 10, 30, 0, TimeSpan.Zero);
        var raw = CacheService.FormatMeta("application/json; charset=utf-8", stored, TimeSpan.FromSeconds(120));

        Assert.True(CacheService.TryParseMeta(raw, out var contentType, out var storedAt, out var ttl));
        Assert.Equal("application/json; charset=utf-8", contentType);
        Assert.Equal(stored, storedAt);
        Assert.Equal(TimeSpan.FromSeconds(120), ttl);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("123|")]
    [InlineData("|0|x")]
    public void Meta_format_rejects_malformed_input(string raw)
    {
        Assert.False(CacheService.TryParseMeta(raw, out _, out _, out _));
    }
}
