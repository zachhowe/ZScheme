using Serilog;
using ZScheme.Toolchain;

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
    ///     predates this call — most likely stale. Only reported once retrying has stopped
    ///     helping, so a handle that is merely passing does not surface as one.
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

    /// <summary>Prefix of the directory an entry is assembled under before it is published.</summary>
    private const string StagingPrefix = ".staging-";

    /// <summary>Prefix of the name an entry being replaced is moved aside under.</summary>
    private const string PreviousPrefix = ".previous-";

    /// <summary>The private name to assemble an entry under, beside its destination —
    ///     the commit is a rename, and that requires a single volume.</summary>
    public static string StagingPathFor(string dest)
    {
        var parent = Path.GetDirectoryName(dest)!;
        SweepStale(parent);
        return Path.Combine(parent, $"{StagingPrefix}{Guid.NewGuid():N}");
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
        for (var attempt = 1; ; attempt++)
        {
            var result = CommitOnce(staging, dest);
            if (result is CommitResult.Committed or CommitResult.PeerWon || attempt == MaxAttempts)
            {
                if (result is CommitResult.Blocked or CommitResult.Failed)
                    Log.Warning(
                        "AtomicDirectory: could not commit {Path} in {Attempts} attempts ({Result})",
                        dest,
                        attempt,
                        result
                    );

                return result;
            }

            // Let the writer that is mid-swap land its rename — or whatever briefly held the
            // entry open drop it — rather than spinning against either.
            Thread.Sleep(BackoffMs * attempt);
        }
    }

    /// <summary>
    ///     How many times a commit that published nothing is retried.
    /// </summary>
    /// <remarks>
    ///     Neither outcome that publishes nothing settles anything from one attempt. A rename that
    ///     fails while the destination is absent is almost always another writer mid-swap, between
    ///     displacing what was there and renaming its own copy in, and a moment later the
    ///     destination is populated again. A destination that cannot be displaced is usually an
    ///     assembly inside it held open — but on Windows that handle is as often a scanner reading
    ///     the .dll a peer just wrote as it is a process with the entry loaded, and it is gone
    ///     within milliseconds. Both leave the staged content untouched and the destination holding
    ///     what it held before, so the whole commit can simply be tried again, and only a
    ///     destination that stays unpublishable across every attempt is reported as anything but a
    ///     success. Judging either from one attempt made eight concurrent writers of the same
    ///     package version report a failed store now and then.
    /// </remarks>
    private const int MaxAttempts = 5;

    /// <summary>Base of the linear backoff between commit attempts, in milliseconds.</summary>
    /// <remarks>
    ///     Long enough to outlast a scanner's handle on a freshly written assembly, which a
    ///     sub-millisecond spin only races against; short enough that the whole retry budget is
    ///     lost noise beside compiling the package that is being cached.
    /// </remarks>
    private const int BackoffMs = 10;

    private static CommitResult CommitOnce(string staging, string dest)
    {
        var parent = Path.GetDirectoryName(dest)!;

        // Free the name for the rename below. Failing while the entry is still there means it
        // cannot be displaced on this attempt — an assembly inside it held open by another process
        // is the usual cause on Windows — and nothing further down could publish over it either,
        // so give up on the attempt and let Commit decide whether the handle was a passing one.
        string? previous = null;
        if (Directory.Exists(dest))
        {
            var aside = Path.Combine(parent, $"{PreviousPrefix}{Guid.NewGuid():N}");
            switch (TryMove(dest, aside))
            {
                case MoveOutcome.Moved:
                    previous = aside;
                    Touch(aside);
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

            Log.Debug("AtomicDirectory: nothing at {Path} to commit onto yet", dest);
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

    /// <summary>
    ///     Deletes scratch directories older than <see cref="ZSchemeHome.StagingMaxAge" /> from
    ///     <paramref name="parent" />.
    /// </summary>
    /// <remarks>
    ///     Both kinds leak. The <c>finally</c> around a commit covers a failure but not a kill
    ///     during the fill, and the restore at the end of <see cref="Commit" /> is best-effort: a
    ///     lost race leaves a displaced entry under its private name for good. Nothing else walks
    ///     these caches, so they accumulate under <c>~/.zscheme</c> with nothing to reclaim them.
    ///     Age-gated for the reason every other staging sweep here is: a concurrent writer's
    ///     scratch is live, and deleting it leaves that process renaming a path that is gone.
    ///     They are invisible to readers either way — every entry is looked up by exact path, and
    ///     a leading '.' is neither a package version nor a cache key.
    /// </remarks>
    private static void SweepStale(string parent)
    {
        var cutoff = DateTime.UtcNow - ZSchemeHome.StagingMaxAge;

        try
        {
            foreach (var prefix in (string[])[StagingPrefix, PreviousPrefix])
            foreach (var stale in Directory.EnumerateDirectories(parent, prefix + "*"))
                if (Directory.GetLastWriteTimeUtc(stale) < cutoff)
                    TryDelete(stale);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best-effort: scratch left behind costs disk, not correctness, and must never be
            // the reason an entry went uncached.
        }
    }

    /// <summary>Stamps a directory as new, so the sweep dates it from now.</summary>
    /// <remarks>
    ///     A rename carries the directory's original timestamps, so an entry cached last month
    ///     arrives under its private name already past the cutoff — and a concurrent commit would
    ///     sweep it while it is still the only copy of what this one displaced.
    ///     <para>
    ///         A caller filling a staging tree needs it for the mirror image: the sweep ages that
    ///         tree off its own timestamp, and writing into a subdirectory of it leaves that
    ///         timestamp where it was, so a long fill has to say it is still alive.
    ///     </para>
    /// </remarks>
    public static void Touch(string path)
    {
        try
        {
            Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A timestamp is not worth failing a commit over.
        }
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
