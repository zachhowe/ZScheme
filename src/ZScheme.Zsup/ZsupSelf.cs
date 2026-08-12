using ZScheme.Toolchain;

namespace ZScheme.Zsup;

/// <summary>Replacing the <c>zsup</c> binary and its shims in place.</summary>
internal static class ZsupSelf
{
    private const string StaleSuffix = ".old-";

    /// <summary>
    ///     Deletes binaries left behind by a previous update. Runs at the start of every invocation,
    ///     because on Windows the file being replaced is still locked at the moment it is renamed.
    /// </summary>
    internal static void SweepStaleBinaries(string? home = null)
    {
        var binDir = ZSchemeHome.GetBinDir(home);
        if (!Directory.Exists(binDir))
            return;

        foreach (var path in Directory.EnumerateFiles(binDir, "*" + StaleSuffix + "*"))
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

    /// <summary>
    ///     Replaces <c>zsup</c> with <paramref name="newBinary" /> and re-stamps the shims.
    /// </summary>
    /// <remarks>
    ///     Windows cannot overwrite or delete a running executable, but it will happily rename one.
    ///     Every managed binary is therefore moved aside before the new one is written; the leftovers
    ///     are deleted by <see cref="SweepStaleBinaries" /> on a later run.
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

        // The shims are replaced too: any of them may be the image currently executing, and a
        // hardlinked shim would otherwise still resolve to the previous zsup.
        foreach (var name in new[] { "zsup" }.Concat(ShimInstaller.ShimNames))
            MoveAside(Path.Combine(binDir, ZSchemeHome.ExeName(name)));

        File.Copy(newBinary, zsupPath, overwrite: true);
        ShimInstaller.MakeExecutable(zsupPath);
        var stamped = ShimInstaller.Install(binDir, zsupPath);

        SweepStaleBinaries(home);
        return stamped;
    }

    private static void MoveAside(string path)
    {
        if (!File.Exists(path))
            return;

        try
        {
            File.Move(path, path + StaleSuffix + Guid.NewGuid().ToString("N")[..8]);
        }
        catch (IOException)
        {
            // Not renameable (unusual). Deleting is fine on Unix, where the running image keeps
            // its inode; if that fails too, the copy below will surface the real error.
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Fall through and let the caller's copy report the problem.
            }
        }
    }
}
