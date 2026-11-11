using System.Reflection;

namespace ElementsCore;

/// <summary>Program version tag (e.g. v226.1111.P1), stamped from version.props at build time.</summary>
public static class BuildInfo
{
    public static string Tag { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "dev";
}
