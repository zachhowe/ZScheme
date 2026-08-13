# Two downloads of the same asset interleave into one fixed `.part` file

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted.

**Affects:** `GitHubReleaseClient.DownloadAssetAsync`, and therefore `zsup install
<version>` and `zsup self update` whenever two of them fetch the same asset name
at once.

## Symptom

Either a checksum failure on a release that is perfectly good:

```
error: checksum mismatch for zscheme-0.4.0-win-x64.zip
  expected 9f2c...  
    actual 41ab...
```

— which sends the user looking for a corrupted release or a hostile mirror when
the bytes they hashed were half theirs and half another process's — or, when the
other process gets to the rename first:

```
error: Could not find file 'C:\Users\me\.zscheme\downloads\zscheme-0.4.0-win-x64.zip.part'.
```

## Root cause

`src/ZScheme.Toolchain/GitHubReleaseClient.cs:169` derives the staging path from
the destination alone:

```csharp
var partPath = destPath + ".part";
```

Every process downloading `zscheme-0.4.0-win-x64.zip` therefore writes the same
`.part` file. The delete-then-create at `:172-173` and `:192` does not serialize
anything — it just decides which process's stream owns the handle — and the four
operations interleave:

| | process A | process B |
| --- | --- | --- |
| 1 | `File.Create(part)`, streams bytes | |
| 2 | | `File.Delete(part)`, `File.Create(part)`, streams bytes |
| 3 | `ComputeSha256(part)` → **B's partial content** | |
| 4 | `File.Move(part, dest)` | |
| 5 | | `ComputeSha256(part)` → `FileNotFoundException` |

On Windows step 2's `File.Delete` fails outright against A's open handle, which
surfaces as a raw `IOException` from a command that had no reason to fail; on
Unix it unlinks the entry out from under A, and A hashes and renames a file
nobody is writing to any more. Either way the process that reports the checksum
mismatch is reporting on bytes it did not download.

`destPath` has the same problem one step later — `File.Move(partPath, destPath,
overwrite: true)` at `:222` is last-writer-wins over a shared archive path — but
that one is benign today, because both writers are fetching the same asset and
every consumer re-hashes what it reads.

## Why this is not theoretical

Two zsup processes are a case this branch designs for everywhere else. Every
other transient it writes already carries a per-process suffix — `.staging-<guid>`
and `.trash-<guid>` (`ToolchainInstaller.cs`), `.zsup-<guid>` (`SelfCommand.cs:123`),
`.tmp-<guid>` (`ToolchainRegistry.WriteSettings`, after 93ab17da), `.old-<guid>`
(`ZsupSelf.cs`) — and `ToolchainInstaller.SweepTransients` refuses to blanket-delete
staging directories specifically because "two terminals, or an editor triggering
one while the user runs another" is expected. `.part` is now the only staging path
in the toolchain that is not private.

The most likely trigger is not two humans: it is CI installing the same pinned
version in two jobs sharing a home, or an editor's install racing the user's.

## Suggested fix direction

1. **Make the `.part` path private to the process**, matching the convention
   already in use. Put the guid *before* the extension rather than after it —
   `destPath + "." + Guid.NewGuid().ToString("N")[..8] + ".part"` — because
   `SweepTransients` matches these with `name.EndsWith(".part")`
   (`src/ZScheme.Toolchain/ToolchainInstaller.cs:365`), so a `.part-<guid>` suffix
   would silently stop being swept and leave a toolchain-sized file in
   `downloads/` after every kill. With that one change every row of the table
   above disappears: each downloader streams into its own slot and hashes what it
   wrote.
2. **Drop the pre-emptive `File.Delete(partPath)` at `:172-173`** once the name is
   private; there is nothing to clear, and the delete is only meaningful for the
   shared name it is compensating for.

`GitHubReleaseClientTests` can cover step 1 against a local `HttpMessageHandler`
stub: assert the download leaves no `*.part*` behind, and that a pre-existing
stale `.part` from a killed run neither affects nor is affected by a fresh
download.

## Priority note

Low frequency, but both failure shapes **misattribute the fault**: one accuses a
good release of being corrupt, and the other is a bare `FileNotFoundException`
from a binary published with `StackTraceSupport=false`. The fix is one line, has
no design questions attached, and brings the last transient in the toolchain in
line with the convention the other five already follow.
