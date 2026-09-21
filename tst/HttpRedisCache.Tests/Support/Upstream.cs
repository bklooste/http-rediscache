using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HttpRedisCache.Tests.Support;

public sealed record Seen(string Method, string PathAndQuery, string Body, Dictionary<string, string> Headers);

/// <summary>
/// A real HTTP server on a random port standing in for the upstream service. By default it answers
/// <c>v1</c>, <c>v2</c>, … — one per call — so tests can tell a cached response from a fresh one.
/// </summary>
public sealed class Upstream : IAsyncDisposable
{
    private readonly WebApplication app;
    private int calls;

    public string BaseUrl { get; }
    public int Calls => Volatile.Read(ref calls);
    public ConcurrentQueue<Seen> Requests { get; } = new();

    /// <summary>What to do with call number <c>n</c> (1-based). Replace it to shape the response.</summary>
    public Func<HttpContext, int, Task> Behavior { get; set; } = (ctx, n) => ctx.Response.WriteAsync($"v{n}");

    private Upstream(WebApplication app, string baseUrl) { this.app = app; BaseUrl = baseUrl; }

    public static async Task<Upstream> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        Upstream? self = null;
        app.Map("/{**path}", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            self!.Requests.Enqueue(new(ctx.Request.Method, ctx.Request.Path + ctx.Request.QueryString,
                body, ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase)));
            await self.Behavior(ctx, Interlocked.Increment(ref self.calls));
        });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return self = new Upstream(app, url);
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}
