using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace HttpRedisCache;

/// <summary>
/// Environment-wide defaults: <c>GLOBAL_</c>-prefixed env vars (e.g. <c>GLOBAL_ConnectionStrings__Redis</c>, as set
/// by a shared env file) are the <em>lowest</em>-precedence layer, directly below <c>appsettings.json</c>. Everything
/// else keeps its place, so <c>DOTNET_</c> variables still override all of it.
/// </summary>
public static class GlobalDefaultsConfiguration
{
    public const string Prefix = "GLOBAL_";

    /// <summary>
    /// Inserts the <c>GLOBAL_</c> source immediately before the <c>appsettings.json</c> source, found by path rather
    /// than index (builders differ in how many sources they add first).
    /// </summary>
    /// <exception cref="InvalidOperationException">The builder has no <c>appsettings.json</c> source.</exception>
    public static IConfigurationBuilder AddGlobalDefaults(this IConfigurationBuilder builder)
    {
        var sources = builder.Sources;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is JsonConfigurationSource { Path: "appsettings.json" })
            {
                sources.Insert(i, new EnvironmentVariablesConfigurationSource { Prefix = Prefix });
                return builder;
            }
        }

        // Never append at the end: there it would outrank appsettings.json and the plain env vars.
        throw new InvalidOperationException($"No appsettings.json configuration source to insert the {Prefix} defaults below.");
    }
}
