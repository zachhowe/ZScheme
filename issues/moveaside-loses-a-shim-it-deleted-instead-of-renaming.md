# `MoveAside`'s delete fallback loses the binary it was asked to park

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted.

**Affects:** `ZsupSelf.MoveAside` (`src/ZScheme.Zsup/ZsupSelf.cs:172-206`), and
therefore both rollback paths in `zsup self update`.

## Symptom

A `zsup self update` that fails and reports a clean rollback, but leaves `zs` or
`zs-lsp` gone from `~/.zscheme/bin` with nothing said about it. The user is told
the previous installation was restored; one of its binaries no longer exists.

## Root cause

`MoveAside` returns the parked path, or `null` for "there was nothing to move":

```csharp
if (!File.Exists(path))
    return null;
```

The fallback path returns the same `null` for a completely different outcome —
the file existed, could not be renamed, and *was deleted*:

```csharp
catch (Exception e) when (e is IOException or UnauthorizedAccessException)
{
    try { File.Delete(path); }
    catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }

    return null;   // <-- deleted, but reported as "nothing was there"
}
```

The caller records only non-null results:

```csharp
if (MoveAside(path) is { } aside)
    movedAside.Add((path, aside));
```

so a deleted name never enters `movedAside`, and `Restore(movedAside)` therefore
cannot put it back and cannot mention it. Both rollback sites are affected — the
`catch` around the move loop and the rename (`:123-135`), and the one around
`ShimInstaller.Install` (`:142`, added in 7cbce1ee).

The sequence that loses a file is:

| | step | state of `zs` |
| --- | --- | --- |
| 1 | `File.Move(zs, zs.old-<guid>)` fails | present |
| 2 | `File.Delete(zs)` succeeds | **gone** |
| 3 | `MoveAside` returns `null`; nothing recorded | gone |
| 4 | a later step throws — the `zsup` rename, or `Install` | gone |
| 5 | `Restore` puts back only what step 3 recorded | **still gone, unreported** |

The comment on the fallback is right about why it deletes ("Deleting is fine on
Unix, where the running image keeps its inode") and right that a subsequent
failure will surface *something* — but what it surfaces is the unrelated later
error, with no mention of the binary this function destroyed.

## Suggested fix direction

Restoration is genuinely impossible once the file is deleted, so the fix is to
stop the loss being silent rather than to prevent it:

1. **Separate the two outcomes.** Return an outcome the caller can distinguish —
   an enum, or a nullable `aside` paired with a `Deleted` flag — rather than
   overloading `null`.
2. **Track deleted names alongside `movedAside`** and have `Restore` name them:
   they belong in the same report as a `.rescue-` park, and for the same reason
   (`Restore`'s remarks: "a name that cannot be put back is ... named out loud").
   The recovery line already exists in the right shape elsewhere —
   `` `zsup self update <version>` `` re-stamps a missing shim.

An alternative is to drop the delete entirely and let the rename failure
propagate, but that gives up the Unix case the fallback exists for; option 2
keeps it and closes the reporting hole.

## Priority note

Low frequency: it needs `File.Move` to fail where `File.Delete` then succeeds,
which is unusual on both platforms, *and* a later failure to trigger a rollback.
The consequence is a missing `zs` that the update's own output says was restored,
which is a bad thing to be wrong about — but it is a reporting fix on a rare
path, not a correctness fix on a common one.
