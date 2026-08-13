# The shim's symlink fallback can leave a dangling `zs` and report it as written

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted.

**Affects:** `ShimInstaller.Build`
(`src/ZScheme.Toolchain/ShimInstaller.cs:139-162`), reached by every
`zsup install` and `zsup self update` on Unix.

## Symptom

`zsup install 0.4.0` finishes, prints `installed toolchain '0.4.0'`, reports no
unstamped shims — and `zs` fails with `No such file or directory`. Nothing in the
output connects the broken `zs` to the install that created it, and the
`WarnAboutUnstampedShims` path that exists for exactly this reporting never fires.

## Root cause

`Build` falls back to a symlink when the hardlink fails, and never checks the
result:

```csharp
if (Link(zsupPath, staged) == 0)
{
    MakeExecutable(staged);
    return;
}

var relative = Path.GetRelativePath(Path.GetDirectoryName(finalPath)!, zsupPath);
File.CreateSymbolicLink(staged, relative);
```

`File.CreateSymbolicLink` does not require the target to exist — a dangling
symlink is a legal symlink, and creating one throws nothing. `InstallOne` then
renames `staged` over `shimPath` successfully, `Install` adds the path to
`written`, and the caller reports a clean stamp:

```csharp
InstallOne(zsupPath, target);
written.Add(target);
```

The comment above the fallback names the case it is guarding
(`ShimInstaller.cs:157-159`) — a filesystem without hardlinks — but `Link` returns
non-zero for any errno, not only `EPERM`/`EXDEV`. If `zsupPath` is not readable
or not present at that instant, the hardlink fails and the symlink succeeds
anyway, pointing at nothing.

The window is real rather than theoretical: `ZsupSelf.ReplaceInstalledBinaries`
moves the zsup binary aside and back around the stamp, and a network or automounted
home can drop between the two.

`InstallOne`'s own remarks make the intended contract explicit
(`ShimInstaller.cs:101-112`) — every failure must degrade to "still the old zsup"
rather than "missing" — and a dangling symlink is precisely "missing" with a
successful return code.

## Suggested fix direction

Verify the link resolves before letting the rename commit it, and let the failure
travel the path already built for it:

```csharp
File.CreateSymbolicLink(staged, relative);

// A dangling symlink is a legal one: CreateSymbolicLink never checks the target.
if (!File.Exists(staged))
    throw new IOException(
        $"created {staged} as a symlink to {relative}, which does not resolve to a file"
    );
```

`File.Exists` follows the link, so it answers the right question, and `IOException`
is already what `Install`'s per-name catch turns into a `Failure` — so the shim
gets named in the warning instead of silently counted as written.

Worth a test in the shim installer tests (Unix only): point `Build` at a
`zsupPath` that does not exist on a filesystem where `link(2)` fails, and assert
the name comes back in `Failed` rather than `Written`.
