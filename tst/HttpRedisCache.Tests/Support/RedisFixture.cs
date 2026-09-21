using StackExchange.Redis;
using Testcontainers.Redis;

namespace HttpRedisCache.Tests.Support;

/// <summary>One real Redis for the whole run. Tests isolate themselves with a unique <c>Cache:KeyPrefix</c>.</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer container = new RedisBuilder("redis:7-alpine").Build();

    public string ConnectionString => container.GetConnectionString();
    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await container.StartAsync();
        Connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        Connection?.Dispose();
        await container.DisposeAsync();
    }

    /// <summary>All keys the cache wrote under <paramref name="keyPrefix"/>.</summary>
    public string[] KeysUnder(string keyPrefix) =>
        Connection.GetServers().SelectMany(s => s.Keys(pattern: keyPrefix + "*")).Select(k => (string)k!).ToArray();
}

[CollectionDefinition("redis")]
public class RedisCollection : ICollectionFixture<RedisFixture>;
