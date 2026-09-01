using Serilog;

namespace ZScheme.Compiler.Cache;

/// <summary>What a call to <see cref="AtomicDirectory.Commit" /> left at the destination.</summary>
internal enum CommitResult
{
    /// <summary>The staged content is the entry at the destination.</summary>
    Committed,

    /// <summary>
    ///     Another writer's entry is at the destination and the staged content was not published.
    ///     A caller whose destination is content-keyed can take that entry for its own — it was
    ///     produced for the same key. A caller whose destination is only a name, a package
    ///     version say, cannot: nothing says the two writers built the same thing.
    /// </summary>
    PeerWon,

    /// <summary>
    ///     The entry that was already at the destination could not be displaced, so it is still
    ///     there and the staged content was not published. An assembly inside it held open by
    ///     another process is the usual cause. The destination therefore holds content that
    ///     predates this call — most likely stale.
    /// </summary>
    Blocked,

    /// <summary>Nothing was published and the destination holds no entry at all.</summary>
    Failed,
}

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
    ///     <para>
    ///         Which of those outcomes actually happened is the caller's to judge, so it is
    ///         returned rather than only logged. Swallowing it meant a store that published
    ///         nothing was indistinguishable from one that published everything, and both callers
    ///         went on to report success — see <see cref="CommitResult" />.
    ///     </para>
    /// </summary>
    public static CommitResult Commit(string staging, string dest)
    {
        var parent = Path.GetDirectoryName(dest)!;

        // Free the name for the rename below. Failing while the entry is still there means it
        // cannot be displaced at all — an assembly inside it held open by another process is the
        // usual cause on Windows — and nothing further down could publish over it either.
        string? previous = null;
        if (Directory.Exists(dest))
        {
            var aside = Path.Combine(parent, $".previous-{Guid.NewGuid():N}");
            switch (TryMove(dest, aside))
            {
                case MoveOutcome.Moved:
                    previous = aside;
                    break;

                // A concurrent writer displaced it between the check and the rename, which frees
                // the name just as well. Only the source going missing says that happened, which
                // is why it is told apart from a rename that failed with the entry still there.
                case MoveOutcome.SourceGone:
                    break;

                default:
                    Log.Warning("AtomicDirectory: could not displace the entry at {Path}", dest);
                    return CommitResult.Blocked;
            }
        }

        if (TryMove(staging, dest) is MoveOutcome.Moved)
        {
            if (previous is not null)
                TryDelete(previous);
            return CommitResult.Committed;
        }

        // From here the staged content did not land, and all that is left to settle is what the
        // destination ends up holding.
        if (previous is not null && Directory.Exists(dest))
        {
            TryDelete(previous);
            Log.Debug("AtomicDirectory: another process committed {Path} first", dest);
            return CommitResult.PeerWon;
        }

        if (previous is null)
        {
            if (Directory.Exists(dest))
            {
                Log.Debug("AtomicDirectory: another process committed {Path} first", dest);
                return CommitResult.PeerWon;
            }

            Log.Warning("AtomicDirectory: could not commit {Path}", dest);
            return CommitResult.Failed;
        }

        // This process holds the only copy of what used to be at dest: put it back rather than
        // leave the cache emptier than it was found.
        if (TryMove(previous, dest) is MoveOutcome.Moved)
        {
            Log.Warning(
                "AtomicDirectory: could not commit {Path}; restored the entry it displaced",
                dest
            );
            return CommitResult.Blocked;
        }

        Log.Warning(
            "AtomicDirectory: could not commit {Path}, and the entry it displaced is left at {Previous}",
            dest,
            previous
        );
        return CommitResult.Failed;
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

    /// <summary>How a rename in <see cref="Commit" /> ended.</summary>
    private enum MoveOutcome
    {
        Moved,

        /// <summary>
        ///     The source was gone by the time the rename ran — another writer renamed it away
        ///     first. Only ever a race between writers, never a lock or a permission.
        /// </summary>
        SourceGone,

        Failed,
    }

    private static MoveOutcome TryMove(string source, string dest)
    {
        try
        {
            Directory.Move(source, dest);
            return MoveOutcome.Moved;
        }
        catch (DirectoryNotFoundException)
        {
            return MoveOutcome.SourceGone;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return MoveOutcome.Failed;
        }
    }
}
