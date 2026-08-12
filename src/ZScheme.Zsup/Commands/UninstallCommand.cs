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
            // Keyed by compiler version, so it is shared by every toolchain built from the same
            // payload. Deleting it because one of them was uninstalled would force the others into
            // a from-source stdlib rebuild, which needs the SDK and the network.
            var sharing = registry.UsingCompilerVersion(compilerVersion);
            if (sharing.Count > 0)
            {
                Console.WriteLine(
                    $"note: kept the package cache for compiler version {compilerVersion}"
                );
                Console.WriteLine(
                    $"      still used by: {string.Join(", ", sharing.Select(t => t.Name))}"
                );
            }
            else
            {
                PurgeCache(ZSchemeHome.GetPackageCacheRootFor(compilerVersion, home));
            }

            // Per-name rather than per-version, so nothing else can be using it.
            PurgeCache(ZSchemeHome.GetLinkedCacheRoot(name, home));
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

    /// <summary>
    ///     Deletes a cache root if it is there, reporting a failure rather than throwing.
    /// </summary>
    /// <remarks>
    ///     The toolchain is already gone by this point, so letting an <see cref="IOException" /> --
    ///     a file locked by a concurrent compile, say -- escape would abort <c>Main</c> and print a
    ///     bare unhandled-exception line (zsup is built with <c>StackTraceSupport=false</c>) about a
    ///     toolchain the user has already been told was removed.
    /// </remarks>
    private static void PurgeCache(string cacheDir)
    {
        if (!Directory.Exists(cacheDir))
            return;

        try
        {
            Directory.Delete(cacheDir, recursive: true);
            Console.WriteLine($"removed package cache {cacheDir}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ZsupHelpers.Warn($"could not remove the package cache at {cacheDir}: {e.Message}");
        }
    }
}
