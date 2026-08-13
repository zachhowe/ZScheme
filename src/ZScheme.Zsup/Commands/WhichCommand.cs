using ZScheme.Toolchain;

namespace ZScheme.Zsup.Commands;

internal static class WhichCommand
{
    internal static int Run(string[] args)
    {
        // Null until named rather than defaulted to "zs", so that a second tool argument is an
        // error instead of silently replacing the first.
        string? tool = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--help" or "-h")
            {
                Console.WriteLine("Usage: zsup which [zs|zs-lsp]");
                return 0;
            }

            // Matched the way the shim itself resolves the name rather than ordinally: on Windows
            // and macOS typing `ZS` launches zs.exe, and ShimInstaller.MatchName is what routes it
            // there. Answering `unknown tool: ZS` for a name that does run is the two disagreeing.
            // The canonical spelling is kept, so the path below is built from ShimNames either way.
            if (ShimInstaller.MatchName(args[i]) is not { } named)
                return ZsupHelpers.Error($"error: unknown tool: {args[i]}");

            if (tool is not null)
                return ZsupHelpers.Error($"error: unexpected argument: {args[i]}");

            tool = named;
        }

        tool ??= "zs";

        var registry = new ToolchainRegistry(ZSchemeHome.GetHome());
        var resolution = new ToolchainResolver(registry).Resolve(
            Environment.GetEnvironmentVariable(ZSchemeHome.VersionEnvironmentVariable),
            ZsupHelpers.CurrentDirectoryOrNull()
        );

        if (resolution is not ToolchainResolution.Resolved resolved)
        {
            Console.Error.WriteLine(ResolutionErrorFormatter.Format(resolution));
            return 1;
        }

        // The path goes to stdout on its own so `$(zsup which zs)` stays usable; the explanation
        // of where the selection came from goes to stderr.
        Console.WriteLine(resolved.Toolchain.GetExecutablePath(tool));
        Console.Error.WriteLine(DescribeOrigin(resolved));
        return 0;
    }

    private static string DescribeOrigin(ToolchainResolution.Resolved resolved)
    {
        return resolved.Origin switch
        {
            ToolchainOrigin.EnvironmentVariable =>
                $"note: '{resolved.Toolchain.Name}' selected by {ZSchemeHome.VersionEnvironmentVariable}",
            ToolchainOrigin.ProjectFile =>
                $"note: '{resolved.Toolchain.Name}' required by {resolved.OriginDetail}",
            _ => $"note: '{resolved.Toolchain.Name}' is the default toolchain",
        };
    }
}
