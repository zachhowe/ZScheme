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

        var registry = new ToolchainRegistry(ZSchemeHome.GetHome());
        try
        {
            registry.Link(name, dir);
        }
        catch (DirectoryNotFoundException)
        {
            return ZsupHelpers.Error($"error: no such directory: {Path.GetFullPath(dir)}");
        }
        catch (IOException e)
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

        Console.WriteLine($"unlinked '{name}'");
        return 0;
    }
}
