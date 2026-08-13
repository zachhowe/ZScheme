using ZScheme.Toolchain;

namespace ZScheme.Zsup;

/// <summary>Replacing the <c>zsup</c> binary and its shims in place.</summary>
internal static class ZsupSelf
{
    private const string StaleSuffix = ".old-";

    /// <summary>
    ///     Suffix of the private slot the incoming zsup is written to before it is renamed into
    ///     place.
    /// </summary>
    private const string StagingSuffix = ".new-";

    /// <summary>
    ///     Suffix a previous binary is parked under when it could not be renamed back after a failed
    ///     update. Deliberately outside what <see cref="SweepStaleBinaries" /> deletes: it is the
    ///     only remaining copy of an installation the user still has to restore by hand.
    /// </summary>
    private const string RescueSuffix = ".rescue-";

    /// <summary>
    ///     Deletes binaries left behind by a previous update. Runs at the start of every invocation,
    ///     because on Windows the file being replaced is still locked at the moment it is renamed.
    /// </summary>
    internal static void SweepStaleBinaries(string? home = null)
    {
        var binDir = ZSchemeHome.GetBinDir(home);
        if (!Directory.Exists(binDir))
            return;

        // A moved-aside binary is dead the instant it is renamed, so it goes as soon as whatever
        // was holding it lets go.
        foreach (var path in Directory.EnumerateFiles(binDir, "*" + StaleSuffix + "*"))
            TryDelete(path);

        // Staging slots are swept too -- the `finally` around the rename covers a failure but not a
        // kill between the copy and the rename, and nothing else walks this directory looking for
        // them -- but age-gated, because unlike a moved-aside binary a staging slot can still be
        // live. The point of naming it per-process is that a concurrent `zsup self update` has one
        // of its own, and deleting that one leaves it renaming a path that no longer exists.
        var cutoff = DateTime.UtcNow - ZSchemeHome.StagingMaxAge;
        foreach (var path in Directory.EnumerateFiles(binDir, "*" + StagingSuffix + "*"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) >= cutoff)
                    continue;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Unreadable timestamp: leave it rather than guess it is abandoned.
                continue;
            }

            TryDelete(path);
        }
    }

    /// <summary>
    ///     Replaces <c>zsup</c> with <paramref name="newBinary" /> and re-stamps the shims.
    /// </summary>
    /// <remarks>
    ///     Windows cannot overwrite or delete a running executable, but it will happily rename one.
    ///     Every managed binary is therefore moved aside before the new one takes its place; the
    ///     leftovers are deleted by <see cref="SweepStaleBinaries" /> on a later run.
    ///     <para>
    ///         The incoming binary is copied into the bin directory first and only then renamed over
    ///         <c>zsup</c>, for the reason <c>ShimInstaller</c> stages a shim: the copy is the step
    ///         that runs out of disk, trips an antivirus scanner or loses a network home, and it must
    ///         not be the step that finds the installation already dismantled. What is left is
    ///         same-directory renames, and a failure in any of them -- moving a name aside as much
    ///         as the rename that replaces zsup -- is rolled back rather than reported over an
    ///         empty bin directory.
    ///     </para>
    /// </remarks>
    /// <returns>
    ///     The shim stamping outcome, for the caller to report. A name that fails here has already
    ///     been moved aside, so it is absent rather than stale -- the update is not complete until
    ///     the user hears about it.
    /// </returns>
    internal static ShimInstaller.Result ReplaceInstalledBinaries(
        string newBinary,
        string? home = null
    )
    {
        var binDir = ZSchemeHome.GetBinDir(home);
        Directory.CreateDirectory(binDir);

        var zsupPath = Path.Combine(binDir, ZSchemeHome.ExeName("zsup"));
        var staged = zsupPath + StagingSuffix + Guid.NewGuid().ToString("N")[..8];

        try
        {
            File.Copy(newBinary, staged, overwrite: true);
            ShimInstaller.MakeExecutable(staged);

            var movedAside = new List<(string Original, string Aside)>();
            try
            {
                // The shims are moved aside too: any of them may be the image currently executing,
                // and a hardlinked shim would otherwise still resolve to the previous zsup.
                foreach (var name in new[] { "zsup" }.Concat(ShimInstaller.ShimNames))
                {
                    var path = Path.Combine(binDir, ZSchemeHome.ExeName(name));
                    if (MoveAside(path) is { } aside)
                        movedAside.Add((path, aside));
                }

                File.Move(staged, zsupPath, overwrite: true);
            }
            catch
            {
                // Nothing is installed at this moment -- not zsup, and not the shims that would let
                // the user re-run the update. The previous binaries go back before the error is
                // allowed out, so a failed update leaves the installation it started from.
                //
                // The loop is inside this try as well as the rename: a bin directory that denies
                // deletes -- populated under sudo, or on a network home with its own ACL -- fails
                // part-way through, and by then the names it did reach are already parked. Rolling
                // back only the rename would leave the user with no zsup to retry with.
                Restore(movedAside);
                throw;
            }

            var stamped = ShimInstaller.Install(binDir, zsupPath);

            SweepStaleBinaries(home);
            return stamped;
        }
        finally
        {
            // A no-op once the rename consumed it.
            TryDelete(staged);
        }
    }

    /// <returns>Where the file was parked, or <c>null</c> when there was nothing to move.</returns>
    private static string? MoveAside(string path)
    {
        if (!File.Exists(path))
            return null;

        var aside = path + StaleSuffix + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.Move(path, aside);
            return aside;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not renameable (unusual). Deleting is fine on Unix, where the running image keeps
            // its inode; if that fails too, the rename below will surface the real error.
            // UnauthorizedAccessException is not an IOException and is what both calls raise when
            // the directory denies deletes, which is the case that leaves bin/ half dismantled.
            try
            {
                File.Delete(path);
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                // Fall through and let the caller's rename report the problem.
            }

            return null;
        }
    }

    /// <summary>Renames the previous binaries back over a replacement that did not happen.</summary>
    /// <remarks>
    ///     Best-effort per name and never allowed to throw: it runs while the exception describing
    ///     the actual failure is on its way out, and that is the one the user needs to read. A name
    ///     that cannot be put back is parked under <see cref="RescueSuffix" /> and named out loud --
    ///     a bare <c>.old-&lt;guid&gt;</c> tells the user nothing about what the file is, and
    ///     <see cref="SweepStaleBinaries" /> would delete it on the next run.
    /// </remarks>
    private static void Restore(List<(string Original, string Aside)> movedAside)
    {
        foreach (var (original, aside) in movedAside)
        {
            try
            {
                File.Move(aside, original, overwrite: true);
                continue;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ZsupHelpers.Warn($"could not restore {original}: {e.Message}");
            }

            var rescue = original + RescueSuffix + Guid.NewGuid().ToString("N")[..8];
            try
            {
                File.Move(aside, rescue);
                Console.Error.WriteLine(
                    $"help: the previous binary is at {rescue}; rename it to {original} to restore it"
                );
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(
                    $"help: the previous binary is at {aside}; rename it to {original} before "
                        + "running zsup again, which sweeps it"
                );
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Still running, or still locked. Swept next time.
        }
    }
}
