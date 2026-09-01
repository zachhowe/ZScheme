using Serilog;

namespace ZScheme.Compiler.Cache;

/// <summary>
///     Publishes a finished directory into a shared cache by rename, so readers only ever see a
///     complete entry.
///     <para>
///         Every cache under <c>~/.zscheme</c> is shared by whatever compiles happen to be running
///         on the machine — the test assemblies <c>dotnet test</c> runs side by side, several
///         <c>zs build</c>s. Filling an entry in place made it visible while it was still being
///         written: readers took a half-populated directory for a finished one, or missed on it and
///         redid the work, colliding with the writer that still held its files open.
///     </para>
/// </summary>
internal static class AtomicDirectory
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(AtomicDirectory));

    /// <summary>The private name to assemble an entry under, beside its destination —
    ///     the commit is a rename, and that requires a single volume.</summary>
    public static string StagingPathFor(string dest)
    {
        var parent = Path.GetDirectoryName(dest)!;
        return Path.Combine(parent, $".staging-{Guid.NewGuid():N}");
    }

    /// <summary>
    ///     Renames <paramref name="staging" /> onto <paramref name="dest" />. Anything already
    ///     there is moved aside first and dropped afterwards: <see cref="Directory.Move" /> has no
    ///     overwrite overload, and deleting before moving would reintroduce — for the width of the
    ///     rename — the half-populated entry staging exists to rule out. Displacing it also repairs
    ///     a broken leftover from a run that was killed mid-write.
    ///     <para>
    ///         Any of these renames can lose to a writer in another process, and none of those
    ///         losses is a failure: both produced the same content for the same cache key, so
    ///         either copy will do. What this must never do is leave the cache emptier than it
    ///         found it, hence the restore at the end.
    ///     </para>
    /// </summary>
    public static void Commit(string staging, string dest)
    {
        var parent = Path.GetDirectoryName(dest)!;

        // Free the name for the rename below. Failing here means a concurrent writer moved the
        // old entry aside first, leaving us nothing to displace.
        string? previous = null;
        if (Directory.Exists(dest))
        {
            var aside = Path.Combine(parent, $".previous-{Guid.NewGuid():N}");
            if (TryMove(dest, aside))
                previous = aside;
        }

        if (!TryMove(staging, dest))
        {
            if (Directory.Exists(dest))
                Log.Debug("AtomicDirectory: another process committed {Path} first", dest);
            else
                Log.Warning("AtomicDirectory: could not commit {Path}", dest);
        }

        if (previous is null)
            return;

        if (Directory.Exists(dest))
            TryDelete(previous);
        else
            TryMove(previous, dest);
    }

    /// <summary>Best-effort cleanup of a scratch directory this process owns.</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Leaving scratch behind is not worth failing the operation it belonged to.
        }
    }

    private static bool TryMove(string source, string dest)
    {
        try
        {
            Directory.Move(source, dest);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
