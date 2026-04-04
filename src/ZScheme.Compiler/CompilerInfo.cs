using System.Reflection;

namespace ZScheme.Compiler;

public static class CompilerInfo
{
    public static string VersionString { get; } = BuildVersionString();

    /// <summary>Base version without git hash suffix (e.g. "0.1.3").</summary>
    public static string BaseVersion { get; } = BuildBaseVersion();

    private static string BuildVersionString()
    {
        var asm = typeof(CompilerInfo).Assembly;

        var infoVersion = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

        var gitTag = asm
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "GitTag")?
            .Value;

        return string.IsNullOrEmpty(gitTag)
            ? infoVersion
            : $"{infoVersion} ({gitTag})";
    }

    private static string BuildBaseVersion()
    {
        var asm = typeof(CompilerInfo).Assembly;
        var infoVersion = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0";

        var plusIndex = infoVersion.IndexOf('+');
        return plusIndex >= 0 ? infoVersion[..plusIndex] : infoVersion;
    }
}
