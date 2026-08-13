# `zsup self uninstall` refuses on Windows even when it is not the installed zsup

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted.

**Affects:** `SelfCommand.RunUninstall`
(`src/ZScheme.Zsup/Commands/SelfCommand.cs:196-201`), reached by
`zsup self uninstall --yes` on Windows.

## Symptom

A Windows CI job — or any teardown script — that runs a repo-built zsup against a
scratch home:

```
> $env:ZSCHEME_HOME = "C:\tmp\zshome"
> .\artifacts\zsup.exe self uninstall --yes
error: zsup cannot remove itself while running on Windows
help: close this shell, then delete C:\tmp\zshome
```

The running binary is in `artifacts\`, nowhere near `C:\tmp\zshome`. Nothing
prevents the delete, and the stated reason is not true of this invocation. The
script has to reimplement the teardown by hand, and the `help:` line tells the
user to close a shell that has nothing to do with the problem.

## Root cause

The refusal is unconditional:

```csharp
// The running binary lives inside the tree being deleted. On Windows that file cannot be
// removed while it is executing, so leave the removal to the user rather than half-deleting
// their home directory.
//
// Ahead of the confirmation gate, not behind it: ...
if (OperatingSystem.IsWindows())
{
    Console.Error.WriteLine($"error: zsup cannot remove itself while running on Windows");
    Console.Error.WriteLine($"help: close this shell, then delete {home}");
    return 1;
}
```

The premise in the first line — "the running binary lives inside the tree being
deleted" — is a claim about *this* process, and it is only true when the running
zsup is the one installed under `home`. The code never checks.

The predicate to check with already exists and is used for the same
"is this path my own bin directory?" question elsewhere:
`ZSchemeHome.IsBinDir(dir, home)` (`ZSchemeHome.cs:81-101`), which is guarded
against unparseable paths and case-folds on Windows. `Environment.ProcessPath`
gives the running binary's directory.

## Suggested fix direction

Make the refusal conditional on the running binary actually being inside the home,
and keep it ahead of the confirmation gate for the reason the existing comment
gives:

```csharp
// Only when the running binary is the installed one: on Windows an executing file cannot be
// deleted, so removing the home would half-delete it. A zsup run from elsewhere -- a repo
// build against a ZSCHEME_HOME scratch directory, which is how CI tears one down -- is not
// in the tree and can remove it perfectly well.
if (OperatingSystem.IsWindows() && IsRunningFromHome(home))
{
    ...
}
```

`IsRunningFromHome` wants to be null-tolerant (`Environment.ProcessPath` is
documented as possibly null) and to answer `true` when it cannot tell, so an
unknown location keeps today's conservative behaviour.

Note the check is `IsBinDir` on the *directory of* `Environment.ProcessPath`,
not on the process path itself. A stricter version would test whether the process
path is anywhere under `home`, since a zsup copied to `<home>/downloads` is
equally undeletable.

Worth a test asserting the refusal does not fire when `ZSCHEME_HOME` points
somewhere the test binary does not live — which is the situation every existing
toolchain test already runs in.
