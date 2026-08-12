using ZScheme.Toolchain;

namespace ZScheme.Zsup.Commands;

internal static class ListCommand
{
    internal static int Run(string[] args)
    {
        var verbose = false;

        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--verbose":
                    verbose = true;
                    break;
                case "--help" or "-h":
                    Console.WriteLine("Usage: zsup list [--verbose]");
                    return 0;
                default:
                    return ZsupHelpers.Error($"error: unknown option: {args[i]}");
            }

        var registry = new ToolchainRegistry(ZSchemeHome.GetHome());
        var toolchains = registry.List();

        if (toolchains.Count == 0)
        {
            Console.WriteLine("no toolchains installed");
            Console.WriteLine("help: run `zsup install latest`");
            return 0;
        }

        var active = ActiveToolchainName(registry);
        var defaultName = registry.GetDefault();

        foreach (var toolchain in toolchains)
        {
            var markers = new List<string>();
            if (toolchain.Name == defaultName)
                markers.Add("default");
            if (toolchain.Name == active)
                markers.Add("active");
            if (toolchain.IsLinked)
                markers.Add(
                    ToolchainRegistry.IsLinkBroken(toolchain)
                        ? $"linked -> {toolchain.LinkTargetPath} (missing)"
                        : $"linked -> {toolchain.LinkTargetPath}"
                );

            var suffix = markers.Count > 0 ? $" ({string.Join(", ", markers)})" : "";
            Console.WriteLine($"{toolchain.Name}{suffix}");

            if (verbose)
                Console.WriteLine($"    {toolchain.BinDir}");
        }

        return 0;
    }

    /// <summary>The toolchain that would run here, or null if resolution fails.</summary>
    private static string? ActiveToolchainName(ToolchainRegistry registry)
    {
        var resolution = new ToolchainResolver(registry).Resolve(
            Environment.GetEnvironmentVariable(ZSchemeHome.VersionEnvironmentVariable),
            Directory.GetCurrentDirectory()
        );

        return resolution is ToolchainResolution.Resolved r ? r.Toolchain.Name : null;
    }
}
