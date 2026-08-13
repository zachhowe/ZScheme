# A deleted working directory kills the shim with one bare line

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted.

**Affects:** `ShimRunner.Run` (`src/ZScheme.Zsup/ShimRunner.cs:42`), on the path
every `zs` and `zs-lsp` invocation takes.

## Symptom

On Unix, where a process may keep running with its working directory unlinked:

```
$ mkdir /tmp/x && cd /tmp/x && rm -rf /tmp/x
$ zs --version
Unhandled exception. System.IO.DirectoryNotFoundException: ...
```

A build script whose scratch directory is cleaned by a sibling step, a `zs` run
from a directory a `git clean` just removed, or an editor's language server
started in a worktree that has since been pruned all land here. The one line is
the whole diagnosis, since zsup is published with `StackTraceSupport=false`.

## Root cause

The resolver is fed the current directory, unguarded:

```csharp
var resolution = new ToolchainResolver(registry).Resolve(
    Environment.GetEnvironmentVariable(ZSchemeHome.VersionEnvironmentVariable),
    Directory.GetCurrentDirectory()
);
```

`Directory.GetCurrentDirectory()` calls `getcwd(3)`, which fails with `ENOENT`
when the directory has been unlinked; .NET surfaces that as
`DirectoryNotFoundException`. Neither `Program.Main` nor `ShimRunner` has a catch
for it.

The same call appears in `WhichCommand.cs:39` and `ListCommand.cs:69`, but those
are manager-mode commands where a crash is merely ugly. `ShimRunner` is the one
place the file's own remarks single out as intolerant of this
(`ShimRunner.cs:157-166`):

> zsup is published with stack trace support off, so an escaping exception is a
> single bare line -- and this is the code path every `zs` invocation takes, where
> that line would be the user's whole diagnosis.

That is the reasoning behind `TryStart`; the very first thing `Run` does escapes it.

## Suggested fix direction

The current directory is only needed to locate a `.zscheme-version` pin, and "no
directory to search" is indistinguishable from "no pin found" — so answering is
both safe and correct:

```csharp
/// <summary>
///     The directory to search for a pin, or null when there is none: on Unix the working
///     directory can be unlinked while a process still runs in it, and getcwd then fails.
/// </summary>
private static string? CurrentDirectoryOrNull()
{
    try
    {
        return Directory.GetCurrentDirectory();
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        return null;
    }
}
```

`ToolchainResolver.Resolve` would need `startDir` to accept null and skip
`VersionFileLocator.Find` — or the helper can return a harmless fallback the
locator will find nothing in. Applying the same helper at the two manager-mode
call sites is worth doing in the same change.

Worth a test on Unix only: chdir into a directory, delete it, and assert the shim
resolves through the global default instead of throwing.
