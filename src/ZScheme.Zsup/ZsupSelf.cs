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
    /// <remarks>
    ///     Both kinds of leftover are age-gated, and for the same reason: a file this sweep can see
    ///     is not necessarily debris. A staging slot belongs to a concurrent <c>zsup self update</c>,
    ///     and deleting it leaves that process renaming a path that no longer exists. A moved-aside
    ///     binary is the rollback copy of an update that may still be in flight — the window between
    ///     <see cref="MoveAside" /> and the rename is short, but <see cref="Program" /> sweeps at the
    ///     start of every manager-mode invocation, so a <c>zsup list</c> in a second terminal is
    ///     enough to land in it. Take those and a failed update has nothing to put back, leaving
    ///     <c>bin/</c> with no zsup, no zs and no zs-lsp: the state <see cref="Restore" /> exists to
    ///     prevent. Nothing waits on the gate on the normal path — an update that gets past the
    ///     rename deletes its own copies itself.
    /// </remarks>
    internal static void SweepStaleBinaries(string? home = null)
    {
        var binDir = ZSchemeHome.GetBinDir(home);
        if (!Directory.Exists(binDir))
            return;

        var cutoff = DateTime.UtcNow - ZSchemeHome.StagingMaxAge;

        foreach (var suffix in new[] { StaleSuffix, StagingSuffix })
        foreach (var path in Directory.EnumerateFiles(binDir, "*" + suffix + "*"))
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
    ///     been moved aside, so it is absent unless the copy could be put back -- either way the
    ///     update is not complete until the user hears about it.
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

            // File.Copy carries the source's write time, and the source was just extracted from a
            // release archive -- whose entries are stamped with the build. Without this the slot is
            // born older than the sweep's cutoff and a concurrent zsup deletes it before the rename
            // below can consume it.
            Stamp(staged);

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

            ShimInstaller.Result stamped;
            try
            {
                stamped = ShimInstaller.Install(binDir, zsupPath);
            }
            catch
            {
                // Every shim is parked at this point and this path leaves none of them behind:
                // Install reports what it could not stamp per name in its result, so what is left
                // to throw is the bin directory itself being unusable -- every name at once.
                // Without the rollback the update ends with a new zsup and no shims at all, the
                // state Restore exists to prevent, and SweepStaleBinaries deletes the only
                // remaining copies an hour later.
                //
                // zsup stays replaced, which is what the per-name path below does too: the rename
                // that put it there has already succeeded, and a shim on the previous zsup is a
                // version mismatch rather than a missing file.
                Restore([
                    .. movedAside.Where(m =>
                        !string.Equals(m.Original, zsupPath, StringComparison.Ordinal)
                    ),
                ]);
                throw;
            }

            // A shim that failed to stamp was already moved aside, so its name is absent and the
            // only working binary left for it is the `.old-<guid>` copy. Putting the copy back
            // leaves the user a shim on the previous zsup: still a version mismatch, and
            // WarnAboutUnstampedShims reads the name off the filesystem, so it reports exactly that
            // instead of a missing file.
            //
            // Matched ordinally: both sides are Path.Combine(binDir, ExeName(name)) built from this
            // same binDir, so the strings are identical rather than merely equivalent.
            var failedPaths = stamped.Failed.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            if (failedPaths.Count > 0)
                Restore([.. movedAside.Where(m => failedPaths.Contains(m.Original))]);

            // This update's own copies, deleted by name rather than left to the sweep. The sweep is
            // age-gated so that a *concurrent* zsup cannot take a rollback copy out from under an
            // update still in flight; this one knows the update is past the point of rolling back,
            // so nothing has to wait an hour to be reclaimed. The names Restore was asked to put
            // back are skipped -- either it consumed them, or it parked what it could not move
            // under `.rescue-` and told the user where to find it.
            foreach (var (original, aside) in movedAside)
                if (!failedPaths.Contains(original))
                    TryDelete(aside);

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

            // Not optional. A rename keeps the file's original timestamps, and that is when the
            // binary was installed -- so a zsup put there last month becomes a `.old-` copy that is
            // already a month old, and a concurrent zsup would sweep it on its very next run. That
            // is precisely the case the age gate in SweepStaleBinaries exists to rule out.
            Stamp(aside);
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
    ///     Best-effort per name and never allowed to throw. On the rollback path it runs while the
    ///     exception describing the actual failure is on its way out, and that is the one the user
    ///     needs to read; on the stamping path it is one name out of an update that otherwise
    ///     succeeded. A name that cannot be put back is parked under <see cref="RescueSuffix" /> and
    ///     named out loud -- a bare <c>.old-&lt;guid&gt;</c> tells the user nothing about what the
    ///     file is, and <see cref="SweepStaleBinaries" /> would delete it on the next run.
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
                    $"help: the previous binary is at {aside}; rename it to {original} before a "
                        + "later zsup run sweeps it"
                );
            }
        }
    }

    /// <summary>
    ///     Stamps a transient as belonging to this update, which is what
    ///     <see cref="SweepStaleBinaries" /> ages it by.
    /// </summary>
    private static void Stamp(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Worst case the file looks older than it is and a concurrent sweep takes it; the
            // update itself has no reason to fail over a timestamp.
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
