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
        // inside the toolchain and is not necessarily the name it was installed under. A linked
        // toolchain has no entry in the shared cache at all -- it compiles into the isolated
        // cache-dev/<name> root instead -- so it gets no compiler version to key one by.
        var isLinked = existing?.IsLinked ?? false;
        var compilerVersion = isLinked
            ? null
            : PackageCacheSeeder.ResolveCompilerVersion(
                existing?.Dir ?? ZSchemeHome.GetToolchainDir(name, home),
                name
            );

        // Set when the toolchain went but the settings file recording it as the default did not.
        string? defaultNotCleared = null;

        try
        {
            registry.Remove(name);
        }
        catch (DirectoryNotFoundException)
        {
            return ZsupHelpers.Error($"error: toolchain '{name}' is not installed");
        }
        catch (ToolchainRegistry.DefaultNotClearedException e)
        {
            // Caught ahead of the general handler below, which it would otherwise match: the
            // toolchain is already gone here, so reporting "could not remove" would send the user
            // back to retry something that has happened. Deferred rather than returned, because the
            // rest of this command -- the cache purge, the note about the default -- still applies.
            defaultNotCleared = e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Removal is a recursive directory delete, which fails against any file still open --
            // on Windows that includes the toolchain's own zs or zs-lsp while it is running, which
            // an editor's language server makes an ordinary situation rather than an edge case.
            // The delete is not transactional, so part of the tree may already be gone.
            return ZsupHelpers.Error(
                $"error: could not remove toolchain '{name}': {e.Message}",
                "help: close anything still running from it (an editor's language server, another shell) and try again",
                $"note: run `zsup install {name} --force` if the installation was left incomplete"
            );
        }

        Console.WriteLine($"removed toolchain '{name}'");

        // The compiled package cache is keyed by compiler version and is expensive to rebuild, so
        // it survives an uninstall unless asked otherwise -- reinstalling the same version then
        // picks the cache back up.
        if (purgeCache)
        {
            // Only for a real installation. A link's name is not a compiler version, and `zsup link
            // 0.4.0 ./build` is legal whenever toolchains/0.4.0 is free -- treating it as one would
            // delete cache/pkg/0.4.0, the released payload's cache, which the link never wrote to.
            if (compilerVersion is not null)
            {
                // Keyed by compiler version, so it is shared by every toolchain built from the same
                // payload. Deleting it because one of them was uninstalled would force the others
                // into a from-source stdlib rebuild, which needs the SDK and the network.
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
            }

            // Per-name rather than per-version, so nothing else can be using it.
            PurgeCache(ZSchemeHome.GetLinkedCacheRoot(name, home));
        }

        if (wasDefault)
        {
            var remaining = registry.List();

            if (defaultNotCleared is not null)
            {
                // The one step past the commit point that can fail -- a read-only home, a full
                // disk. Every later `zs` resolves the default and finds a toolchain that is not
                // there, so the user needs to know, but the removal itself did happen.
                ZsupHelpers.Warn(defaultNotCleared);
                Console.Error.WriteLine(
                    remaining.Count > 0
                        ? $"help: run `zsup use <toolchain>` once that is fixed ({string.Join(", ", remaining.Select(t => t.Name))})"
                        : "help: run `zsup install latest` once that is fixed"
                );
                return 0;
            }

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
