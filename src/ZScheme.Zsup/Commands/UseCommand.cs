using ZScheme.Toolchain;

namespace ZScheme.Zsup.Commands;

internal static class UseCommand
{
    internal static int Run(string[] args)
    {
        string? name = null;
        var local = false;

        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--local":
                    local = true;
                    break;
                case "--help" or "-h":
                    Console.WriteLine("Usage: zsup use <toolchain> [--local]");
                    return 0;
                default:
                    if (args[i].StartsWith('-'))
                        return ZsupHelpers.Error($"error: unknown option: {args[i]}");
                    if (name is not null)
                        return ZsupHelpers.Error($"error: unexpected argument: {args[i]}");
                    name = args[i];
                    break;
            }

        if (name is null)
            return ZsupHelpers.Error(
                "error: expected a toolchain name",
                "usage: zsup use <toolchain> [--local]"
            );

        if (!ToolchainName.IsValid(name))
            return ZsupHelpers.Error($"error: invalid toolchain name: '{name}'");

        var registry = new ToolchainRegistry(ZSchemeHome.GetHome());
        var toolchain = registry.TryGet(name);
        if (toolchain is null)
            return ZsupHelpers.Error(
                $"error: toolchain '{name}' is not installed",
                $"help: run `zsup install {name}`, or `zsup list` to see what is available"
            );

        // TryGet deliberately returns a link whose target is gone rather than null, and documents
        // that callers distinguish it with IsLinkBroken. ToolchainResolver.Select and ListCommand
        // both do; this one did not, so `zsup use` -- the command whose entire job is selecting
        // something usable -- reported success and left the home with a default that every
        // subsequent `zs` fails to resolve. Ahead of the --local branch so both paths get it, and
        // routed through the formatter rather than restating its wording, so the message the shim
        // gives for the same state cannot drift from this one.
        if (ToolchainRegistry.IsLinkBroken(toolchain))
            return ZsupHelpers.Error(
                ResolutionErrorFormatter.Format(
                    new ToolchainResolution.LinkBroken(name, toolchain.Dir)
                )
            );

        if (local)
        {
            var pin = Path.Combine(Directory.GetCurrentDirectory(), ZSchemeHome.VersionFileName);

            try
            {
                File.WriteAllText(pin, name + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return ZsupHelpers.Error($"error: could not write {pin}: {e.Message}");
            }

            Console.WriteLine($"pinned '{name}' in {pin}");
            return 0;
        }

        try
        {
            registry.SetDefault(name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unlike `zsup install`, recording the default is the whole command here, so a failed
            // write is a failed command rather than a warning.
            return ZsupHelpers.Error($"error: could not record the default toolchain: {e.Message}");
        }

        Console.WriteLine($"default toolchain is now '{name}'");

        // A pin above the current directory silently outranks the default we just set, which would
        // otherwise look like the command did nothing.
        var overriding = VersionFileLocator.Find(ZsupHelpers.CurrentDirectoryOrNull());
        if (overriding is not null && !ToolchainName.AreSame(overriding.ToolchainName, name))
            ZsupHelpers.Warn(
                $"{overriding.FilePath} pins '{overriding.ToolchainName}', which takes precedence here"
            );

        if (
            Environment.GetEnvironmentVariable(ZSchemeHome.VersionEnvironmentVariable) is { } env
            && env.Trim().Length > 0
            && !ToolchainName.AreSame(env.Trim(), name)
        )
            ZsupHelpers.Warn(
                $"{ZSchemeHome.VersionEnvironmentVariable} is set to '{env.Trim()}', which takes precedence"
            );

        return 0;
    }
}
