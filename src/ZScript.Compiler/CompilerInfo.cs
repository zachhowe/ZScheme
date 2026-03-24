using System.Reflection;

namespace ZScript.Compiler;

public static class CompilerInfo
{
    public static string VersionString { get; } = BuildVersionString();

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
}
