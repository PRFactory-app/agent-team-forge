using System.Reflection;

namespace AgentTeamForge.Host.Hosting;

/// <summary>The release version stamped at build time (-p:InformationalVersion), without the +commit suffix.</summary>
internal static class ProductVersion
{
    public static string Current { get; } =
        typeof(ProductVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
}
