using HttpRedisCache.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace HttpRedisCache.Tests;

/// <summary>
/// GLOBAL_ env vars are environment-wide defaults: lowest precedence, below appsettings.json; DOTNET_ beats both.
/// Env vars are process-wide, hence the serial collection.
/// </summary>
[Collection("redis")]
public class ConfigPrecedenceTests(RedisFixture redis) : IDisposable
{
    private const string Key = "Precedence__Probe";
    private readonly string dir = Directory.CreateTempSubdirectory("precedence-").FullName;

    /// <summary>The same source order the service builds: appsettings.json, then GLOBAL_ inserted below it, then DOTNET_.</summary>
    private IConfigurationRoot Build(string? appsettingsValue)
    {
        File.WriteAllText(Path.Combine(dir, "appsettings.json"),
            appsettingsValue is null ? "{}" : $$"""{ "Precedence": { "Probe": "{{appsettingsValue}}" } }""");
        var builder = new ConfigurationBuilder()
            .AddJsonFile(new PhysicalFileProvider(dir), "appsettings.json", optional: true, reloadOnChange: false)
            .AddGlobalDefaults()
            .AddEnvironmentVariables("DOTNET_");
        return builder.Build();
    }

    [Fact]
    public void A_GLOBAL_variable_alone_is_read()
    {
        Environment.SetEnvironmentVariable("GLOBAL_" + Key, "global");
        Assert.Equal("global", Build(null)["Precedence:Probe"]);
    }

    [Fact]
    public void Appsettings_beats_GLOBAL()
    {
        Environment.SetEnvironmentVariable("GLOBAL_" + Key, "global");
        Assert.Equal("appsettings", Build("appsettings")["Precedence:Probe"]);
    }

    [Fact]
    public void DOTNET_beats_appsettings_and_GLOBAL()
    {
        Environment.SetEnvironmentVariable("GLOBAL_" + Key, "global");
        Environment.SetEnvironmentVariable("DOTNET_" + Key, "dotnet");
        Assert.Equal("dotnet", Build("appsettings")["Precedence:Probe"]);
    }

    [Fact]
    public void Without_an_appsettings_source_it_throws_rather_than_append_on_top()
    {
        Assert.Throws<InvalidOperationException>(() => new ConfigurationBuilder().AddGlobalDefaults());
    }

    [Fact]
    public void The_service_starts_from_GLOBAL_ConnectionStrings__Redis_alone()
    {
        Environment.SetEnvironmentVariable("GLOBAL_ConnectionStrings__Redis", redis.ConnectionString);
        using var app = new CacheApp(new());

        app.CreateClient().Dispose(); // throws if the connection string were not picked up
    }

    public void Dispose()
    {
        foreach (var name in new[] { "GLOBAL_" + Key, "DOTNET_" + Key, "GLOBAL_ConnectionStrings__Redis" })
            Environment.SetEnvironmentVariable(name, null);
        Directory.Delete(dir, recursive: true);
    }
}
