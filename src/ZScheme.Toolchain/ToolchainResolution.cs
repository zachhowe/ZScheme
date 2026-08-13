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

    /// <summary>Nothing selected a toolchain, and the home holds none to select.</summary>
    public sealed record NoToolchains : ToolchainResolution;

    /// <summary>
    ///     Nothing selected a toolchain, but toolchains are installed -- no default is set.
    /// </summary>
    /// <remarks>
    ///     Distinct from <see cref="NoToolchains" /> because the two need opposite advice, and this
    ///     one is not the exotic case: a first install with <c>--no-default</c>, an uninstall of the
    ///     toolchain that was the default, and a settings file too corrupt to read all land here.
    /// </remarks>
    /// <param name="Available">The installed names, never empty.</param>
    public sealed record NoDefault(IReadOnlyList<string> Available) : ToolchainResolution;
}
