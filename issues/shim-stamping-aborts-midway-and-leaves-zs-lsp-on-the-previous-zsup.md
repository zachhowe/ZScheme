# A locked `zs.exe` aborts shim stamping partway, leaving `zs-lsp` pointing at the previous zsup

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager), not by a test or a user report. Traced from source; no live repro
attempted.

**Affects:** `zsup install` and `zsup self update` on Windows, whenever any shim
binary in `~/.zscheme/bin` is locked at the moment the command runs. The common
case is an editor holding `zs-lsp.exe`, or a `zs` still running in another
terminal.

## Symptom

`zsup install 0.5.0` prints

```
warning: could not refresh the shims in C:\Users\me\.zscheme\bin: The process cannot access the file 'zs.exe' because it is being used by another process.
```

and exits 0. The user sees one warning about "the shims", plural, and reasonably
reads it as "no shims were touched". What actually happened depends on which name
was locked and where the loop stopped.

## Root cause

`ShimInstaller.Install` (`src/ZScheme.Toolchain/ShimInstaller.cs:49-62`) stamps the
names in a bare loop with no per-name isolation:

```csharp
var written = new List<string>();
foreach (var name in ShimNames)          // ["zs", "zs-lsp"]
{
    var target = Path.Combine(binDir, ZSchemeHome.ExeName(name));
    InstallOne(zsupPath, target);        // throws -> loop dies here
    written.Add(target);
}
```

`InstallOne` (`:64-92`) deletes the existing file before writing the replacement:

```csharp
if (File.Exists(shimPath))
    File.Delete(shimPath);
```

On Windows a running image cannot be deleted, so `File.Delete` throws
`IOException` and the exception leaves the whole loop. The only handler is at the
call site, `InstallCommand.StampShims` (`src/ZScheme.Zsup/Commands/InstallCommand.cs:219-226`),
which catches it and emits the single warning quoted above.

The states the loop can leave behind:

| locked | `zs` | `zs-lsp` | reported |
| --- | --- | --- | --- |
| neither | new | new | nothing |
| `zs` | **deleted or old** | **old** | one warning |
| `zs-lsp` | new | **deleted or old** | one warning |

Two of these break the invariant the class documents at `ShimInstaller.cs:10-13`:

> Always re-stamped by `zsup install` and `zsup self update`, so the shims can
> never drift out of sync with the `zsup` next to them.

The middle row is the worst of the three: `zs` is stamped first, so a lock on it
stops the loop before `zs-lsp` is even attempted, and the user is left with a
`zs-lsp` from the *previous* zsup while `zsup` and `zs` are current. That is
exactly the mixed-version state the invariant exists to rule out, and nothing
reports which name is stale.

There is a second, smaller hole in `InstallOne`: the delete-then-write sequence is
not atomic, so a shim that is deleted and then fails to be re-created leaves *no*
`zs` at all. `File.Copy(..., overwrite: true)` on the next line would have handled
the Windows case without the delete; the delete is there for the Unix hardlink
case (`:66-69` explains why), which does not apply on Windows.

## Why the return value does not help

`Install` returns `IReadOnlyList<string>` — "the paths that were written" — but
the throwing path discards it, so a partial stamp returns nothing rather than the
prefix it managed. A caller cannot distinguish "wrote both" from "wrote `zs`,
died on `zs-lsp`" from "died on `zs`" without inspecting the filesystem itself.

## Suggested fix direction

1. **Stamp each name independently and collect failures.** Wrap each `InstallOne`
   in its own try/catch, keep going, and return both the written paths and the
   failed ones. That holds the invariant as far as it can be held and turns the
   single vague warning into one line per name that actually failed.
2. **Name the drift in the warning.** "could not refresh `zs-lsp` (in use); it
   still points at the previous zsup — close your editor and run `zsup install
   --force`" is actionable in a way the current text is not. This is the part that
   matters most: a stale `zs-lsp` is silent, and the next symptom the user sees is
   an unrelated-looking language-server bug that was fixed two releases ago.
3. **Drop the pre-delete on Windows.** `File.Copy(overwrite: true)` fails against
   a locked file just as the delete does, but it fails without having destroyed
   the existing shim first, so a locked `zs` degrades to "still the old one"
   rather than "gone". The delete stays on Unix, where `InstallOne` needs it to
   break hardlinks to the old inode.
4. Consider whether the retry belongs here at all: `zsup` could detect that a
   running process holds the shim and say so up front, rather than discovering it
   halfway through.

`ShimInstallerTests` already covers the happy path and the Unix link fallbacks; a
test for this needs a locked-file fixture, which is Windows-only — mark it
`[SkippableFact]` or gate it the way the platform-specific tests in
`ToolchainInstallerTests` are.

## Priority note

Above ordinary polish: it produces a **silently mixed toolchain** on the most
common Windows setup (an editor running while the user upgrades), the warning
actively misleads, and the invariant it violates is one other code and docs rely
on. It is not urgent — the user can re-run `zsup install --force` after closing
the editor, and the state self-corrects — but nothing tells them they need to.
