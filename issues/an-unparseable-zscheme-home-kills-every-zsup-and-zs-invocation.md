# An unparseable `ZSCHEME_HOME` kills every `zsup` and every `zs` with a bare line

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted.

**Affects:** `PathNormalizer.Normalize`
(`src/ZScheme.Toolchain/PathNormalizer.cs:31`), reached from
`ZSchemeHome.GetHome` and `ZSchemeHome.GetEffectiveCacheRoot` — that is, from
every zsup command and from the shim's hot path.

## Symptom

```
$ export ZSCHEME_HOME='%NOT_SET%'      # expands to itself, or to empty
$ zs --version
Unhandled exception. System.ArgumentException: ...
$ zsup list
Unhandled exception. System.ArgumentException: ...
```

zsup ships with `<StackTraceSupport>false</StackTraceSupport>`
(`src/ZScheme.Zsup/ZScheme.Zsup.csproj:13`), so that one line is the user's entire
diagnosis — for a misconfigured variable that every other path-parsing site in
this branch reports as an ordinary error. The same applies to `ZSCHEME_CACHE_DIR`
through `GetEffectiveCacheRoot`.

## Root cause

`Normalize` ends with an unguarded `GetFullPath`:

```csharp
var expanded = Environment.ExpandEnvironmentVariables(trimmed);
...
return Path.GetFullPath(expanded);
```

`GetFullPath` throws `ArgumentException` for a path containing invalid characters
or resolving to empty, and `PathTooLongException` for an over-long one.
`ExpandEnvironmentVariables` makes the empty case easy to reach: a `%VAR%`
reference to a variable set to the empty string expands to nothing, and
`IsNullOrWhiteSpace` was checked *before* the expansion, not after.

There is no top-level handler to catch it. `Program.Main`
(`src/ZScheme.Zsup/Program.cs:12-42`) and `ZsupCli.Run`
(`src/ZScheme.Zsup/ZsupCli.cs:8-27`) both dispatch without a try, and the call
sits on:

- the shim path — `ShimRunner.Run:38`, taken by every `zs` and `zs-lsp`;
- `InstallCommand.cs:54`, `UseCommand.cs:39`, `ListCommand.cs:24`,
  `UninstallCommand.cs:39`, `LinkCommand.cs:40`, `SelfCommand.cs:183`.

Every comparable site in this branch already catches the triple and answers
instead of throwing — `ZSchemeHome.IsBinDir` (`ZSchemeHome.cs:93-100`),
`ToolchainRegistry.ReadLinkTarget` (`ToolchainRegistry.cs:341-353`),
`ToolchainInstaller.FullPathOrNull` (`ToolchainInstaller.cs:498-504`),
`LinkCommand`'s own normalization (`LinkCommand.cs:34-38`). `IsBinDir`'s comment
states the standard this one misses:

> Answering rather than throwing matters here: every `zs` invocation asks this
> question.

## Suggested fix direction

Two layers, and both are worth having:

1. **Make `Normalize` answer.** Catching and returning `null` makes an
   unusable override fall through to the next source (`ZSCHEME_HOME`, then
   `~/.zscheme`), which is the same degradation `ReadSettings` and `List` already
   choose for the shim's hot path. If silently ignoring the variable is too quiet,
   throw a typed `ArgumentException` with the variable named, and catch it in each
   command — but note that leaves the shim path needing its own handler.

2. **Add a top-level catch in `Program.Main`** for the exceptions that are
   genuinely unexpected, printing `error: <message>` rather than
   `Unhandled exception.` — with stack traces compiled out, the current output
   carries strictly less information than a formatted line would.

Worth a test on `ZSchemeHome.GetHome(explicitOverride, envValue)` — the testable
overload already exists — asserting a malformed and an empty-expansion value both
fall back rather than throw.
