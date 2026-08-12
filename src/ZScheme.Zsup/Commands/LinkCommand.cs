using ZScheme.Toolchain;

namespace ZScheme.Zsup.Commands;

internal static class LinkCommand
{
    internal static int RunLink(string[] args)
    {
        if (args is ["--help" or "-h", ..])
        {
            Console.WriteLine("Usage: zsup link <name> <dir>");
            return 0;
        }

        if (args.Length != 2)
            return ZsupHelpers.Error(
                "error: expected a name and a directory",
                "usage: zsup link <name> <dir>"
            );

        var (name, dir) = (args[0], args[1]);
        if (!ToolchainName.IsValid(name))
            return ZsupHelpers.Error($"error: invalid toolchain name: '{name}'");

        // Normalized here rather than inside ToolchainRegistry.Link so that a path the OS will not
        // even parse -- `zsup link dev "C:\a|b"` -- is an ordinary error message instead of an
        // ArgumentException escaping Main. Passing the absolute path on also keeps the error below
        // from having to resolve it a second time.
        string full;
        try
        {
            full = Path.GetFullPath(dir);
        }
        catch (Exception e)
            when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return ZsupHelpers.Error($"error: not a usable directory path: {dir}");
        }

        var registry = new ToolchainRegistry(ZSchemeHome.GetHome());
        try
        {
            registry.Link(name, full);
        }
        catch (DirectoryNotFoundException)
        {
            return ZsupHelpers.Error($"error: no such directory: {full}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ZsupHelpers.Error($"error: {e.Message}");
        }

        var toolchain = registry.TryGet(name)!;
        Console.WriteLine($"linked '{name}' -> {toolchain.Dir}");

        if (!File.Exists(toolchain.GetExecutablePath("zs")))
            ZsupHelpers.Warn(
                $"no {ZSchemeHome.ExeName("zs")} found in {toolchain.BinDir}; "
                    + "build the project before using this toolchain"
            );

        return 0;
    }

    internal static int RunUnlink(string[] args)
    {
        if (args is ["--help" or "-h", ..])
        {
            Console.WriteLine("Usage: zsup unlink <name>");
            return 0;
        }

        if (args.Length != 1)
            return ZsupHelpers.Error("error: expected a name", "usage: zsup unlink <name>");

        var name = args[0];
        if (!ToolchainName.IsValid(name))
            return ZsupHelpers.Error($"error: invalid toolchain name: '{name}'");

        try
        {
            new ToolchainRegistry(ZSchemeHome.GetHome()).Unlink(name);
        }
        catch (FileNotFoundException)
        {
            return ZsupHelpers.Error($"error: no linked toolchain named '{name}'");
        }
        catch (ToolchainRegistry.DefaultNotClearedException e)
        {
            // Ahead of the handler below, which it would otherwise match. The link is already gone
            // by this point, so this is the settings write that failed after it -- a warning on top
            // of a completed unlink, not a failure that contradicts it.
            Console.WriteLine($"unlinked '{name}'");
            ZsupHelpers.Warn(e.Message);
            Console.Error.WriteLine("help: run `zsup use <toolchain>` once that is fixed");
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A read-only home, or a link file held open. Without this the exception escapes Main,
            // and zsup is built with StackTraceSupport=false, so all the user would see is a bare
            // unhandled-exception line.
            return ZsupHelpers.Error($"error: could not unlink '{name}': {e.Message}");
        }

        Console.WriteLine($"unlinked '{name}'");
        return 0;
    }
}
