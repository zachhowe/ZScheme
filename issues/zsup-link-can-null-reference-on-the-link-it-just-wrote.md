# `zsup link` can null-reference on the link it just wrote

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted.

**Affects:** `LinkCommand.RunLink`
(`src/ZScheme.Zsup/Commands/LinkCommand.cs:54-55`), reached by
`zsup link <name> <dir>`.

## Symptom

`zsup link dev C:\build` writes the link file, then dies with a bare
`NullReferenceException` line before printing anything — for a link that was in
fact created successfully. The user has no way to tell from the output whether to
re-run, and re-running succeeds, which makes it look like a transient nothing.

## Root cause

The command re-reads what it just wrote and asserts it is there:

```csharp
registry.Link(name, full);
...
var toolchain = registry.TryGet(name)!;
Console.WriteLine($"linked '{name}' -> {toolchain.Dir}");
```

`TryGet` returns null for a link file whose target cannot be read, because
`ReadLinkTarget` swallows the failure by design (`ToolchainRegistry.cs:341-353`):

> An unreadable or malformed link file behaves as if it were absent, rather than
> making every command fail.

Two ways to get there between the write and the read:

- **A concurrent zsup deletes the file.** `zsup unlink dev` calls `File.Delete`
  (`ToolchainRegistry.cs:225`), and `ToolchainInstaller.InstallFrom`'s `--force`
  path deletes the link past its commit point (`ToolchainInstaller.cs:159-160`).
  `TryGet` then finds neither a directory nor a link file and returns null at
  `:136` — before `ReadLinkTarget` is even reached.
- **A scanner holds the fresh file on Windows.** `ReadLines` raises `IOException`,
  `ReadLinkTarget` returns null, `TryGet` returns null at `:139`.

The `!` turns both into an NRE at `:55`, which no catch on the path filters for —
`RunLink`'s three catches are all above, around `registry.Link`.

Concurrency here is not exotic: the branch's design notes repeatedly cite "an
editor's install racing the user's" and "two terminals" as the cases the staging
scheme exists for.

## Suggested fix direction

Report the link from what the command already knows, rather than reading it back.
`full` is the resolved target and is what was written:

```csharp
registry.Link(name, full);
Console.WriteLine($"linked '{name}' -> {full}");

var toolchain = registry.TryGet(name);
if (toolchain is null)
    return 0;   // Something removed it underneath us; the link itself succeeded.
```

The two `File.Exists` warnings below need `toolchain.BinDir`, so they can be
skipped in that case — they are advisories about a link that no longer exists.
Alternatively construct the `InstalledToolchain` locally via the same
`ResolveBinDir` logic, which avoids the re-read entirely; that would want
`ToolchainRegistry.Link` to return the entry it created.

Worth a test that deletes the link file between `Link` and the read — or, more
simply, one asserting `RunLink` does not throw when `TryGet` answers null.
