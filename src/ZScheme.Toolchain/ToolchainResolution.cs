namespace ZScheme.Toolchain;

/// <summary>Where a toolchain selection came from, in precedence order.</summary>
public enum ToolchainOrigin
{
    EnvironmentVariable,
    ProjectFile,
    GlobalDefault,
}

/// <summary>The outcome of resolving which toolchain to run.</summary>
public abstract record ToolchainResolution
{
    private ToolchainResolution() { }

    /// <summary>A usable toolchain was selected.</summary>
    public sealed record Resolved(
        InstalledToolchain Toolchain,
        ToolchainOrigin Origin,
        string? OriginDetail
    ) : ToolchainResolution;

    /// <summary>Something selected a toolchain by name, but it is not installed.</summary>
    public sealed record NotInstalled(string Name, ToolchainOrigin Origin, string? OriginDetail)
        : ToolchainResolution;

    /// <summary>A linked toolchain was selected but its target directory has gone.</summary>
    public sealed record LinkBroken(string Name, string TargetPath) : ToolchainResolution;

    /// <summary>Nothing selected a toolchain and there is no default.</summary>
    public sealed record NoToolchains : ToolchainResolution;
}
