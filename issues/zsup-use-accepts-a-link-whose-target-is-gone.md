# `zsup use` accepts a link whose target is gone

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted.

**Affects:** `UseCommand.Run` (`src/ZScheme.Zsup/Commands/UseCommand.cs:39-45`),
reached by `zsup use <name>`.

## Symptom

```
$ zsup link dev C:\build
linked 'dev' -> C:\build

... C:\build is deleted (a cleaned worktree, a wiped CI scratch dir) ...

$ zsup use dev
default toolchain is now 'dev'
$ echo $?
0

$ zs --version
error: linked toolchain 'dev' points at C:\build, which no longer exists
help: run `zsup unlink dev`, or `zsup link dev <dir>` to re-point it
```

`use` is the command whose entire job is selecting something usable, and it is
the one command that reports success here. Every subsequent `zs` fails, and the
home is left with a default that cannot resolve.

## Root cause

`use` treats a non-null `TryGet` as "installed and selectable":

```csharp
var toolchain = registry.TryGet(name);
if (toolchain is null)
    return ZsupHelpers.Error(
        $"error: toolchain '{name}' is not installed",
        ...
    );
```

But `TryGet` deliberately returns a broken link rather than null — that is
documented on it (`ToolchainRegistry.cs:120-124`):

> A link pointing at a missing directory is still returned; callers distinguish
> that case by checking `IsLinkBroken`.

`UseCommand` is the caller that does not. The two that do both handle it:
`ToolchainResolver.Select` turns it into `LinkBroken`
(`ToolchainResolver.cs:53-54`), which is where the `zs` error above comes from,
and `ListCommand` marks it `(missing)` (`ListCommand.cs:49-51`).

Note that `SetDefault` cannot catch it either — it validates the *name*, not the
target.

## Suggested fix direction

Add the check the doc comment asks for, between the null test and the `--local`
branch so both paths get it:

```csharp
if (ToolchainRegistry.IsLinkBroken(toolchain))
    return ZsupHelpers.Error(
        $"error: linked toolchain '{name}' points at {toolchain.Dir}, which no longer exists",
        $"help: run `zsup unlink {name}`, or `zsup link {name} <dir>` to re-point it"
    );
```

That is the message `ResolutionErrorFormatter` already produces for the same
state (`ResolutionErrorFormatter.cs:17-21`); routing through it rather than
restating it keeps the two from drifting.

Worth a test alongside `ToolchainRegistryTests.IsLinkBroken_DetectsAMissingTarget`:
link a directory, delete it, and assert `zsup use` returns non-zero and leaves
`settings.json` untouched.
