# Two zsup processes writing the default toolchain race on a fixed `settings.json.tmp`

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; the window is small and no live repro was attempted.

**Affects:** every `WriteSettings` caller — `zsup use`, `zsup install` (which sets
the new toolchain as the default), `zsup uninstall` (which clears it via
`ClearDefaultIf`), and `zsup unlink`.

## Symptom

Either

```
error: Could not find file 'C:\Users\me\.zscheme\settings.json.tmp'.
```

as an unhandled `FileNotFoundException` (zsup is built with
`StackTraceSupport=false`, so this is the entire output), or — more often — no
error at all and a `settings.json` whose `defaultToolchain` belongs to the *other*
process's command. The user runs `zsup use 0.4.0`, is told `default toolchain is
now '0.4.0'`, and the file says `0.5.0`.

## Root cause

`ToolchainRegistry.WriteSettings` (`src/ZScheme.Toolchain/ToolchainRegistry.cs:270-284`)
stages through a name derived only from the destination:

```csharp
// Write-then-rename so an interrupted write cannot leave a truncated settings file.
var temp = path + ".tmp";
File.WriteAllText(temp, json + Environment.NewLine);
File.Move(temp, path, overwrite: true);
```

The rename is atomic; the *staging slot* is not. Two processes share one
`settings.json.tmp`, so the four operations can interleave:

| | process A (`zsup use 0.4.0`) | process B (`zsup install 0.5.0`) |
| --- | --- | --- |
| 1 | `WriteAllText(tmp, "…0.4.0…")` | |
| 2 | | `WriteAllText(tmp, "…0.5.0…")` |
| 3 | `Move(tmp, settings.json)` | |
| 4 | | `Move(tmp, settings.json)` → **`FileNotFoundException`** |

A crashes nothing but writes B's content under its own success message (step 3
moves the bytes B wrote at step 2). B then throws from `File.Move` because its
temp file is already gone. Reorder steps 2 and 3 and the loser is A instead. A
second, rarer shape: both `WriteAllText` calls open the same path concurrently and
one fails outright with a sharing violation on Windows.

The read side is already hardened — `ReadSettings` (`:256-267`) treats a corrupt
file as "no default" so zsup stays usable — but no caller guards the write.

## Why this is not theoretical

Concurrent zsup invocations are a case this branch **designs for elsewhere**.
`ToolchainInstaller.SweepTransients` (`src/ZScheme.Toolchain/ToolchainInstaller.cs:313-322`)
refuses to blanket-delete staging directories precisely because of it:

> A blanket delete would destroy the staging tree of a second `zsup install`
> running concurrently — two terminals, or an editor triggering one while the user
> runs another …

Both of those scenarios end in `SetDefault`. And every other transient in this
branch already carries a per-process suffix — `.staging-<guid8>`, `.trash-<guid8>`,
`.zsup-<guid8>` (`SelfCommand.cs:112`), `.old-` — so `settings.json.tmp` is the
one staging path that does not.

## Suggested fix direction

1. **Give the temp file a per-process suffix**, matching the convention already in
   use: `path + ".tmp-" + Guid.NewGuid().ToString("N")[..8]`. That alone removes
   both failure modes above — each writer stages privately and the `File.Move`
   becomes a genuine last-writer-wins, which is the right semantics for "set the
   default".
2. **Delete the temp on a failed move**, in a `finally`, so a crash between write
   and rename does not leave `settings.json.tmp-abc12345` behind forever. Unlike
   `.staging-`/`.part`, these live in the home root rather than `downloads/`, so
   `SweepTransients` will never see them — either sweep the home root too or make
   the cleanup unconditional here.
3. Decide whether last-writer-wins is actually enough. A read-modify-write lock
   (an `O_EXCL` lock file, or `FileShare.None` on the settings file itself) would
   also protect the `ReadSettings` → mutate → `WriteSettings` sequence in
   `SetDefault`/`ClearDefault` (`:117-129`), where a concurrent write between the
   read and the write is silently lost. Today `ToolchainSettings` holds one
   meaningful field, so the lost update is invisible; that stops being true the
   moment a second setting is added. Worth doing when the settings file grows,
   not necessarily before.

`ToolchainRegistryTests` has no concurrency coverage. A test for step 1 can be
deterministic without threads: assert that `WriteSettings` leaves no
`settings.json.tmp` in the home root, and that a pre-existing stale
`settings.json.tmp` (from a killed process) does not affect a subsequent write.

## Priority note

Low-frequency, but the failure is **silent data loss on a user-visible setting**
in the most likely shape, and an unhandled exception with no stack trace in the
other. The fix for step 1 is two lines and has no design questions attached, so
the cost/benefit is unusually good; steps 2 and 3 can wait.
