using System.Reflection;

namespace SnowPatcher.App;

internal static class ModioConfiguration
{
    public static string? ReadApiKey() => Environment.GetEnvironmentVariable("SNOWPATCHER_MODIO_API_KEY") ??
        typeof(ModioConfiguration).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "SnowPatcher.ModioApiKey")?.Value;
}
