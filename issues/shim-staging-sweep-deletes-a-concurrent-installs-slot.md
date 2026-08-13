# The shim staging sweep is not age-gated, so it deletes a concurrent install's slot

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; the window is small and no live repro was attempted.

**Affects:** Windows only, and every caller of `ShimInstaller.Install` — `zsup
install`, `zsup self update`, and the installer scripts. This is the same race
93ab17da fixed for `settings.json`, reintroduced one file over.

## Symptom

Two zsup processes stamping shims at the same time — two terminals, or an editor
triggering an install while the user runs another. The loser reports a shim it
could not refresh, for a shim that is in fact perfectly healthy:

```
warning: could not refresh `zs`: Could not find file 'C:\Users\me\.zscheme\bin\zs.exe.tmp-a1b2c3d4'.
help: C:\Users\me\.zscheme\bin\zs.exe still points at the previous zsup; close whatever is using it and run `zsup install 0.4.0 --force`
```

The advice is unfollowable: nothing is holding `zs.exe`, so re-running produces
the same warning whenever the two processes overlap again, and the user is left
believing they have a mixed-version installation.

## Root cause

`ShimInstaller.InstallOne` (`src/ZScheme.Toolchain/ShimInstaller.cs:112-123`)
stages under a per-process name, which is right:

```csharp
SweepStaging(shimPath);

var staged = shimPath + StagingSuffix + Guid.NewGuid().ToString("N")[..8];
try
{
    File.Copy(zsupPath, staged, overwrite: true);
    File.Move(staged, shimPath, overwrite: true);
}
finally
{
    TryDeleteFile(staged);
}
```

But the sweep that runs first (`:155-172`) deletes **every** `zs.exe.tmp-*` next
to the shim, with no age gate:

```csharp
foreach (
    var stale in Directory.EnumerateFiles(
        Path.GetDirectoryName(shimPath)!,
        Path.GetFileName(shimPath) + StagingSuffix + "*"
    )
)
    TryDeleteFile(stale);
```

The private slot stops being private the moment another process sweeps it:

| | process A (`zsup install 0.4.0`) | process B (`zsup install 0.5.0`) |
| --- | --- | --- |
| 1 | `SweepStaging` (nothing to do) | |
| 2 | `File.Copy(zsup, zs.exe.tmp-a1b2c3d4)` | |
| 3 | | `SweepStaging` → **deletes `zs.exe.tmp-a1b2c3d4`** |
| 4 | `File.Move(zs.exe.tmp-a1b2c3d4, zs.exe)` → **`FileNotFoundException`** | |

`Install` (`:89-92`) catches that as a `Failure`, and `ZsupHelpers
.WarnAboutUnstampedShims` turns it into the message above. The outcome is
cosmetic on Windows — B's stamp still lands, so the shim is current — but the
report is wrong in the one direction that matters: it tells the user their shims
have drifted when they have not, and the recovery it prints cannot clear it.

## Why this is not theoretical

The identical problem, one file over, already has the fix. `ToolchainRegistry
.SweepStaleStaging` (`src/ZScheme.Toolchain/ToolchainRegistry.cs:353-365`) gates
on `StagingMaxAge` (one hour) and its remark states the reason in as many words:

> Age-gated because the point of the private slot is that a concurrent zsup has
> one too: unlinking a live one on Unix would leave that process renaming a path
> that no longer exists, which is the race this staging scheme exists to remove.

That is exactly what `SweepStaging` does. Concurrent zsup invocations are a case
this branch designs for elsewhere — `ToolchainInstaller.SweepTransients`
(`src/ZScheme.Toolchain/ToolchainInstaller.cs:313-322`) refuses to blanket-delete
staging directories for the same reason — and both of those scenarios reach
`ShimInstaller.Install`.

## Suggested fix direction

1. **Age-gate `SweepStaging` the way `SweepStaleStaging` is gated.** Lift
   `StagingMaxAge` somewhere both can see it (it belongs next to the staging
   suffixes rather than duplicated), and skip anything written inside the cutoff.
   A live slot is at most seconds old; a slot a killed run left behind is swept
   an hour later, which is soon enough for something that costs disk and nothing
   else.
2. Consider whether the sweep needs to run *before* the copy at all. It exists
   only to stop killed runs accumulating slots, so running it after a successful
   stamp — or only when the directory holds more than a couple — would take it
   off the path where it can collide at all. The age gate is the smaller change;
   this is the tidier one.

`ShimInstallerTests` can cover step 1 without threads: create a `zs.exe.tmp-<guid>`
with a current timestamp, run `Install`, and assert it survives; back-date the
same file past the cutoff and assert it is gone.

## Priority note

Low severity — the shim itself ends up correct on both processes, so nothing is
lost or corrupted. It is worth fixing anyway because the failure mode is a
**false report of the exact drift this class exists to rule out**, and because
the fix is the age gate already written and commented two files away.
