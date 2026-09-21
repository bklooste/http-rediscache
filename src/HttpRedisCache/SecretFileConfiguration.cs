namespace HttpRedisCache;

/// <summary>
/// Docker/k8s secret support: for any env var <c>X__Y_FILE=/path</c>, sets <c>X:Y</c> to the file's
/// contents. Lets secrets be mounted instead of passed as plain env vars (which show in
/// <c>docker inspect</c>). A <c>_FILE</c> value overrides the plain one. A leading <c>DOTNET_</c> is
/// dropped, so <c>DOTNET_ConnectionStrings__Redis_FILE</c> works like the plain form.
/// </summary>
public static class SecretFileConfiguration
{
    private const string DotnetPrefix = "DOTNET_";

    public static IConfigurationBuilder AddSecretFiles(this IConfigurationBuilder builder)
    {
        var values = new Dictionary<string, string?>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var name = (string)e.Key;
            if (!name.EndsWith("_FILE", StringComparison.Ordinal) || e.Value is not string path) continue;
            if (!File.Exists(path)) throw new FileNotFoundException($"{name} points at a missing file", path);
            var key = name[..^"_FILE".Length];
            if (key.StartsWith(DotnetPrefix, StringComparison.Ordinal)) key = key[DotnetPrefix.Length..];
            values[key.Replace("__", ":")] = File.ReadAllText(path).Trim();
        }
        return builder.AddInMemoryCollection(values);
    }
}
