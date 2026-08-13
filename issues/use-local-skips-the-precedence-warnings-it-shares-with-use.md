# `zsup use --local` skips the precedence warnings the global path prints

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted.

**Affects:** `UseCommand.Run` (`src/ZScheme.Zsup/Commands/UseCommand.cs:47-62`),
reached by `zsup use <name> --local`.

## Symptom

With `ZSCHEME_VERSION` exported — direnv, a CI job, a devcontainer:

```
$ export ZSCHEME_VERSION=0.3.0
$ zsup use 0.4.0 --local
pinned '0.4.0' in S:\repo\.zscheme-version
$ zs --version
zs 0.3.0
```

The pin was written and is simply outranked. The user is told the command
succeeded and gets no hint about the variable that overrides it — which is the
outcome the warnings further down the same method exist to prevent.

## Root cause

The `--local` branch returns before them:

```csharp
if (local)
{
    ...
    Console.WriteLine($"pinned '{name}' in {pin}");
    return 0;          // <-- lines 79-92 never run
}
```

Everything after that point is the global path's, including:

```csharp
// A pin above the current directory silently outranks the default we just set, which would
// otherwise look like the command did nothing.
var overriding = VersionFileLocator.Find(Directory.GetCurrentDirectory());
...
if (Environment.GetEnvironmentVariable(ZSchemeHome.VersionEnvironmentVariable) is { } env ...)
    ZsupHelpers.Warn(
        $"{ZSchemeHome.VersionEnvironmentVariable} is set to '{env.Trim()}', which takes precedence"
    );
```

The comment states the reasoning — "would otherwise look like the command did
nothing" — and it applies to a pin exactly as it applies to a default. The
precedence order is `ZSCHEME_VERSION` > nearest `.zscheme-version` > global
default (`ToolchainResolver.Resolve`), so only the *environment* half is relevant
to `--local`; the pin-file half is not, since the file just written in the current
directory is the nearest one by definition.

## Suggested fix direction

Hoist the environment warning above the `--local` return so both paths print it,
and leave the pin-file warning where it is:

```csharp
static void WarnIfEnvOverrides(string name) { ... }   // lines 85-92, extracted

if (local)
{
    ...
    Console.WriteLine($"pinned '{name}' in {pin}");
    WarnIfEnvOverrides(name);
    return 0;
}
```

One caveat worth honouring in the message: a `.zscheme-version` in a *sub*directory
does not outrank the one just written, so the pin-file warning must not simply be
shared as-is.

Worth a test asserting `zsup use x --local` warns when `ZSCHEME_VERSION` names
something else — `ToolchainResolver` is already environment-injectable, so the
warning helper should take the value rather than read it, the way
`ZSchemeHome.GetHome`'s testable overload does.
