# `zsup uninstall` reports success but leaves the link behind when a name has both a directory and a `.link`

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager).

**Affects:** `ToolchainRegistry.Remove`, and therefore `zsup uninstall <name>`,
for any name that carries both `toolchains/<name>/` and `toolchains/<name>.link`.

## Symptom

```
$ zsup uninstall 0.4.0
removed toolchain '0.4.0'

$ zs --version
ZScheme 0.4.0-dev+abc1234        # ... from S:\src\ZScheme\artifacts, the link the user thought was gone
```

The name the user just removed is still selectable, still resolvable as the
default, and still what `zs` runs — now pointing at whatever directory the old
link named, which may be a build tree that has since moved on or been deleted. A
link whose target is gone gives the worse version of this: `zsup uninstall`
reports success and every later `zs` fails to resolve a toolchain that the user
has already removed once.

## Root cause

`ToolchainRegistry.Remove` (`src/ZScheme.Toolchain/ToolchainRegistry.cs:196-210`)
treats the two representations as mutually exclusive:

```csharp
if (Directory.Exists(dir))
    Directory.Delete(dir, recursive: true);
else if (File.Exists(linkFile))
    File.Delete(linkFile);
else
    throw new DirectoryNotFoundException($"Toolchain '{name}' is not installed");
```

When both exist the `else if` never runs. `UninstallCommand` then prints
`removed toolchain '{name}'` (`src/ZScheme.Zsup/Commands/UninstallCommand.cs:88`)
because nothing threw, and `TryGet` — which prefers the directory — now finds the
surviving link file and hands it back as the toolchain for that name.

## Why this is not theoretical

The codebase already knows about this exact state and says so. `ToolchainInstaller
.InstallFrom` (`src/ZScheme.Toolchain/ToolchainInstaller.cs:56-63`) guards against
creating it, and the comment names this bug as the reason:

> The reciprocal of the guard in `ToolchainRegistry.Link`. Without it a name can
> end up with both a directory and a `.link` file, which `List` reports twice and
> `zsup uninstall` only half removes — leaving the stale link as the toolchain
> that name now selects.

`List` carries the same acknowledgement (`ToolchainRegistry.cs:74-77`): "The
installer refuses to create that collision, but a home predating it — or one
edited by hand — can still have one".

The collision is reachable through the installer's own `--force` path, which is
the case the guards deliberately let through. `InstallFrom` under `--force`
creates `toolchains/<name>/` and then deletes the link file *past the commit
point*, best-effort, warning when it cannot (`ToolchainInstaller.cs:146-155`):

> `'{name}'` still has a link file at `{linkFile}`; remove it with `zsup unlink {name}`

Anything holding that file — an antivirus scanner on Windows, a read-only home —
leaves exactly the state `Remove` half-handles, and the recovery zsup prints for
it (`zsup unlink`) is not the command a user reaches for after being told the
toolchain was removed.

## Suggested fix direction

1. **Delete both when both are present.** Make `Remove` unconditional in each
   branch and throw only when neither existed:

   ```csharp
   var removed = false;
   if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); removed = true; }
   if (File.Exists(linkFile)) { File.Delete(linkFile); removed = true; }
   if (!removed) throw new DirectoryNotFoundException($"Toolchain '{name}' is not installed");
   ```

   Order matters for the failure case: deleting the directory first means an
   `IOException` from a locked payload leaves the link in place, which is the
   state `UninstallCommand`'s existing "could not remove" handler already
   describes. The reverse order would delete the link and then fail, silently
   changing what the name resolves to.
2. Consider whether `Unlink` (`:182-192`) wants the same treatment. It throws
   `No linked toolchain named '{name}'` when the link file is absent, which is
   right — but after step 1 it is the only remaining way to reach a half-state,
   and it is what the installer's warning tells users to run.

`ToolchainRegistryTests` can cover this directly: create both `toolchains/x/` and
`toolchains/x.link`, call `Remove("x")`, and assert `TryGet("x")` is null and
neither path exists.

## Priority note

Low frequency — it needs a `--force` install whose link delete failed, or a home
edited by hand — but the failure is **a success message for something that did
not happen**, and what survives silently redirects every subsequent `zs`
invocation. The fix is three lines in one method, and the two comments quoted
above show the shape of the bug was already understood when the guards were
written.
