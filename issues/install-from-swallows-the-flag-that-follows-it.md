# `install --from` swallows the flag that follows it

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted.

**Affects:** `InstallCommand.Run`
(`src/ZScheme.Zsup/Commands/InstallCommand.cs:18-27`), reached by
`zsup install ... --from ...`.

## Symptom

```
$ zsup install --from --force 0.4.0
installing '0.4.0' from S:\repo\--force
error: No such archive or directory: S:\repo\--force
```

The user meant `--from <dir> --force` and mistyped the order or lost an argument
to shell expansion. `--force` is silently consumed as the value of `--from`, and
the error names a path they never typed. Every other option in the command
rejects a stray `-`-prefixed token (`:40-41`), so this one reads as inconsistent.

## Root cause

The guard covers only the final-position case, then takes the next token
unconditionally:

```csharp
case "--from":
    // Not a `when` guard on the case: falling through to `default:` would report a
    // trailing `--from` as an unknown option, which it is not.
    if (i + 1 >= args.Length)
        return ZsupHelpers.Error(
            "error: --from needs a value",
            "usage: zsup install <version> --from <archive|dir>"
        );
    from = args[++i];
    break;
```

`args[++i]` accepts anything, including another option. The comment explains why
the guard is not a `when` clause on the case — which is right — but the guard it
enables only tests `i + 1 >= args.Length`, not what `args[i + 1]` actually is.

Note the same shape does not appear elsewhere in zsup: `--from` is the only option
in the whole manager that takes a value.

## Suggested fix direction

Test the value, not just its existence:

```csharp
case "--from":
    if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
        return ZsupHelpers.Error(
            "error: --from needs a value",
            "usage: zsup install <version> --from <archive|dir>"
        );
    from = args[++i];
    break;
```

A path that genuinely begins with `-` is unreachable through this check, which is
the standard trade-off and matches how the `default:` arm already treats such
tokens; `./-weird` is the escape hatch, and it is worth naming in the help line if
that is a concern.

Worth a test in the install-command argument-parsing tests: assert
`--from --force` is rejected, and that a trailing `--from` still gives the
existing message rather than "unknown option".
