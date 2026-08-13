# `zsup which` prints a path to a binary that is not there

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted.

**Affects:** `WhichCommand.Run` (`src/ZScheme.Zsup/Commands/WhichCommand.cs:42-52`),
reached by `zsup which zs` / `zsup which zs-lsp`.

## Symptom

```
$ zsup link dev ./src/ZScheme.Cli/bin/Debug/net10.0
linked 'dev' -> S:\...\bin\Debug\net10.0
warning: no zs-lsp.exe found in ...; editors using this toolchain will fail to
         start a language server

$ zsup which zs-lsp
S:\...\bin\Debug\net10.0\zs-lsp.exe
note: 'dev' is the default toolchain
$ echo $?
0
```

The file does not exist. `$(zsup which zs-lsp)` is the documented way to point an
editor at the language server, so this hands the editor a dead path and exits
successfully while doing it.

## Root cause

`which` stops at resolution and never checks the executable:

```csharp
if (resolution is not ToolchainResolution.Resolved resolved)
{
    Console.Error.WriteLine(ResolutionErrorFormatter.Format(resolution));
    return 1;
}

Console.WriteLine(resolved.Toolchain.GetExecutablePath(tool));
```

`Resolved` only means a toolchain was selected and, for a link, that its target
directory exists (`ToolchainResolver.cs:53`). It says nothing about the binary.
`GetExecutablePath` is pure string composition over
`InstalledToolchain.BinDir`, and `ToolchainRegistry.ResolveBinDir` explicitly
falls back to the conventional `<dir>/bin` when neither shape holds
(`ToolchainRegistry.cs:325-327`):

> Neither exists yet (a broken link, or a partially written install). Report the
> conventional location so error messages point somewhere meaningful.

That is the right answer for an error message and the wrong one for stdout.

The gap is reachable in exactly the case `LinkCommand` already warns about
(`LinkCommand.cs:63-78`): the CLI and the language server are separate projects
with separate output directories, so linking one of them gives a working `zs` and
no `zs-lsp` at all. A half-extracted install reaches it too.

`ShimRunner` — the thing `which` claims to describe — does check, and answers
differently (`ShimRunner.cs:75-85`): it prints `toolchain 'dev' has no zs-lsp`,
names the expected path, and returns 127. The two disagree about the same
question.

## Suggested fix direction

Check the file and fail the way the shim does, keeping stdout clean so the
command-substitution use stays honest:

```csharp
var path = resolved.Toolchain.GetExecutablePath(tool);

if (!File.Exists(path))
{
    Console.Error.WriteLine($"error: toolchain '{resolved.Toolchain.Name}' has no {tool}");
    Console.Error.WriteLine($"note: expected it at {path}");
    return 1;
}
```

`ShimRunner`'s existing linked-vs-installed `help:` line is worth reusing
verbatim so the two paths keep giving the same advice.

Worth a test in the zsup command tests: link a directory holding only `zs`, and
assert `zsup which zs-lsp` returns non-zero and writes nothing to stdout.
