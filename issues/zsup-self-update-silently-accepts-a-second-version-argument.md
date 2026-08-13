# `zsup self update` silently takes the last of several version arguments

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager).

**Affects:** `zsup self update` only. Every other command that takes a positional
already rejects a second one.

## Symptom

```
$ zsup self update 0.4.0 0.5.0
downloading zscheme-zsup-0.5.0-win-x64.tar.gz
updated zsup to 0.5.0
```

The user asked for 0.4.0 and got 0.5.0, with nothing in the output marking the
discrepancy. The realistic shapes are a stray shell expansion, a copy-paste that
picked up a trailing token, or a half-edited command line — and the result is a
manager binary at a version the user did not choose. Since `zsup self update`
replaces `zs` and `zs-lsp` alongside `zsup`, an accidental downgrade re-stamps
every shim in the installation.

## Root cause

`SelfCommand.RunUpdate` (`src/ZScheme.Zsup/Commands/SelfCommand.cs:33-44`) assigns
the positional with no "already set" guard:

```csharp
default:
    if (args[i].StartsWith('-'))
        return ZsupHelpers.Error($"error: unknown option: {args[i]}");
    version = args[i];
    break;
```

Every other command in the CLI has the guard. `InstallCommand.cs:42-44`:

```csharp
if (spec is not null)
    return ZsupHelpers.Error($"error: unexpected argument: {args[i]}");
spec = args[i];
```

and `UseCommand.cs:24` and `UninstallCommand.cs:24` do the same. `self update` is
the one that was missed, so it is also the one where the mistake is silent.

`RunUninstall` in the same file (`:167-175`) has the opposite gap in a harmless
direction: its `default:` rejects everything, so a stray positional is already an
error there.

## Suggested fix direction

Match the three commands that get it right:

```csharp
default:
    if (args[i].StartsWith('-'))
        return ZsupHelpers.Error($"error: unknown option: {args[i]}");
    if (version is not null)
        return ZsupHelpers.Error($"error: unexpected argument: {args[i]}");
    version = args[i];
    break;
```

Worth checking the rest of the CLI for the same shape while in there — the guard
is easy to leave out precisely because the command works without it.

## Priority note

Low. It needs a malformed command line to trigger, and the outcome is recoverable
by running the intended `zsup self update <version>` afterwards. It is on the list
because the fix is two lines, the pattern to copy is three files away, and the
failure is silent: the one thing a version argument exists to make explicit is the
thing this drops.
