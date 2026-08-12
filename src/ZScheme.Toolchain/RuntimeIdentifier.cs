using System.Runtime.InteropServices;

namespace ZScheme.Toolchain;

/// <summary>Maps the running machine to the RID whose release assets it should download.</summary>
public static class RuntimeIdentifier
{
    /// <summary>Every RID releases are published for.</summary>
    public static readonly string[] Supported =
    [
        "win-x64",
        "win-arm64",
        "linux-x64",
        "linux-arm64",
        "osx-x64",
        "osx-arm64",
    ];

    /// <summary>
    ///     The current machine's RID.
    /// </summary>
    /// <remarks>
    ///     Computed from the OS and process architecture rather than read from
    ///     <see cref="RuntimeInformation.RuntimeIdentifier" />, which under Native AOT reports the
    ///     RID the binary was *built* for and can say things like <c>linux-musl-x64</c>.
    /// </remarks>
    public static string Detect()
    {
        return TryDetect()
            ?? throw new PlatformNotSupportedException(
                "ZScheme has no release build for this platform. Supported: "
                    + string.Join(", ", Supported)
            );
    }

    /// <summary>The current machine's RID, or <c>null</c> if it is not a supported combination.</summary>
    public static string? TryDetect()
    {
        var os =
            OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() ? "linux"
            : null;

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };

        if (os is null || arch is null)
            return null;

        var rid = $"{os}-{arch}";
        return Supported.Contains(rid) ? rid : null;
    }

    /// <summary>The archive extension used for a RID: <c>.zip</c> on Windows, <c>.tar.gz</c> elsewhere.</summary>
    public static string ArchiveExtension(string rid)
    {
        return rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz";
    }
}
