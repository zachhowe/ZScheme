using ZScheme.Toolchain;

namespace ZScheme.Zsup.Commands;

internal static class UninstallCommand
{
    internal static int Run(string[] args)
    {
        string? name = null;
        var purgeCache = false;

        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--purge-cache":
                    purgeCache = true;
                    break;
                case "--help" or "-h":
                    Console.WriteLine("Usage: zsup uninstall <toolchain> [--purge-cache]");
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
                "usage: zsup uninstall <toolchain> [--purge-cache]"
            );

        if (!ToolchainName.IsValid(name))
            return ZsupHelpers.Error($"error: invalid toolchain name: '{name}'");

        var home = ZSchemeHome.GetHome();
        var registry = new ToolchainRegistry(home);
        var existing = registry.TryGet(name);
        var wasDefault = ToolchainName.AreSame(registry.GetDefault(), name);

        // Read before removal: the cache key is the payload's compiler version, which is recorded
        // inside the toolchain and is not necessarily the name it was installed under.
        var compilerVersion =
            existing is not null && !existing.IsLinked
                ? PackageCacheSeeder.ResolveCompilerVersion(existing.Dir, name)
                : name;

        try
        {
            registry.Remove(name);
        }
        catch (DirectoryNotFoundException)
        {
            return ZsupHelpers.Error($"error: toolchain '{name}' is not installed");
        }

        Console.WriteLine($"removed toolchain '{name}'");

        // The compiled package cache is keyed by compiler version and is expensive to rebuild, so
        // it survives an uninstall unless asked otherwise -- reinstalling the same version then
        // picks the cache back up.
        if (purgeCache)
        {
            var cacheDir = ZSchemeHome.GetPackageCacheRootFor(compilerVersion, home);
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, recursive: true);
                Console.WriteLine($"removed package cache {cacheDir}");
            }

            var linkedCache = ZSchemeHome.GetLinkedCacheRoot(name, home);
            if (Directory.Exists(linkedCache))
            {
                Directory.Delete(linkedCache, recursive: true);
                Console.WriteLine($"removed package cache {linkedCache}");
            }
        }

        if (wasDefault)
        {
            var remaining = registry.List();
            Console.WriteLine("note: that was the default toolchain");
            Console.WriteLine(
                remaining.Count > 0
                    ? $"help: run `zsup use <toolchain>` to pick another ({string.Join(", ", remaining.Select(t => t.Name))})"
                    : "help: run `zsup install latest`"
            );
        }

        return 0;
    }
}
