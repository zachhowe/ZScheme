namespace ZScheme.Toolchain;

/// <summary>
///     Renders an unresolved <see cref="ToolchainResolution" /> as the message the shim prints to
///     stderr. Kept pure and separate from the shim so the exact wording is unit-assertable.
/// </summary>
public static class ResolutionErrorFormatter
{
    /// <summary>
    ///     Formats the error. Passing a <see cref="ToolchainResolution.Resolved" /> is a caller bug.
    /// </summary>
    public static string Format(ToolchainResolution resolution)
    {
        return resolution switch
        {
            ToolchainResolution.NotInstalled n => FormatNotInstalled(n),
            ToolchainResolution.LinkBroken b => string.Join(
                Environment.NewLine,
                $"error: linked toolchain '{b.Name}' points at {b.TargetPath}, which no longer exists",
                $"help: run `zsup unlink {b.Name}`, or `zsup link {b.Name} <dir>` to re-point it"
            ),
            ToolchainResolution.NoToolchains => string.Join(
                Environment.NewLine,
                "error: no ZScheme toolchain is installed",
                "help: run `zsup install latest`"
            ),
            ToolchainResolution.NoDefault d => string.Join(
                Environment.NewLine,
                "error: no default toolchain is selected",
                // Named when there is only one, because then the help line is the whole answer and
                // the user has nothing to choose between.
                d.Available.Count == 1
                    ? $"help: run `zsup use {d.Available[0]}`"
                    : "help: run `zsup use <toolchain>`; `zsup list` shows what is installed"
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(resolution),
                resolution,
                "Not an error resolution"
            ),
        };
    }

    private static string FormatNotInstalled(ToolchainResolution.NotInstalled n)
    {
        return n.Origin switch
        {
            ToolchainOrigin.ProjectFile => string.Join(
                Environment.NewLine,
                $"error: toolchain '{n.Name}' is not installed",
                $"note: required by {n.OriginDetail}",
                $"help: run `zsup install {n.Name}`"
            ),
            ToolchainOrigin.EnvironmentVariable => string.Join(
                Environment.NewLine,
                $"error: toolchain '{n.Name}' is not installed",
                $"note: selected by {ZSchemeHome.VersionEnvironmentVariable}",
                $"help: run `zsup install {n.Name}`, or unset {ZSchemeHome.VersionEnvironmentVariable}"
            ),
            _ => string.Join(
                Environment.NewLine,
                $"error: the default toolchain '{n.Name}' is not installed",
                $"help: run `zsup install {n.Name}`, or `zsup use <toolchain>` to select another"
            ),
        };
    }
}
