namespace HttpRedisCache.Tests;

/// <summary>Keeps the README config table honest: config is the public API of a container.</summary>
public class ReadmeConfigTableTests
{
    private static string Readme()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "README.md"));
    }

    [Fact]
    public void Every_option_is_documented_in_the_readme()
    {
        var readme = Readme();

        var settings = typeof(CacheOptions).GetProperties()
            .Select(p => $"{CacheOptions.SectionName}__{p.Name}")
            .Append("ConnectionStrings__Redis") // the one required setting lives outside CacheOptions
            .ToList();

        var missing = settings.Where(env => !readme.Contains($"`{env}`")).ToList();
        Assert.True(missing.Count == 0, $"README config table is missing: {string.Join(", ", missing)}");
    }

    [Fact]
    public void The_readme_states_each_default_next_to_its_setting()
    {
        var readme = Readme();
        var defaults = new CacheOptions();

        var expected = new Dictionary<string, string>
        {
            ["Cache__DefaultTtlSeconds"] = defaults.DefaultTtlSeconds.ToString(),
            ["Cache__StaleWhileRevalidateSeconds"] = defaults.StaleWhileRevalidateSeconds.ToString(),
            ["Cache__UpstreamTimeoutSeconds"] = defaults.UpstreamTimeoutSeconds.ToString(),
            ["Cache__KeyPrefix"] = defaults.KeyPrefix,
        };

        foreach (var (env, value) in expected)
        {
            var row = readme.Split('\n').FirstOrDefault(l => l.Contains($"`{env}`"));
            Assert.True(row is not null && row.Contains($"`{value}`"), $"README row for {env} must show its default `{value}` (got: {row})");
        }
    }
}
