# One junk `PATH` entry turns a finished install into an unhandled exception

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted.

**Affects:** `ZsupDoctor.WarnIfBinDirNotOnPath` and `ZsupDoctor.PathsEqual`
(`src/ZScheme.Zsup/ZsupDoctor.cs:12-54`), reached at `InstallCommand.cs:123`.

## Symptom

`zsup install 0.4.0` extracts the toolchain, commits it, stamps the shims, records
the default, prints every one of those, and *then* exits non-zero with a bare
unhandled-exception line — because one entry in the user's `PATH` is over the
length limit. Scripts keyed on the exit code treat a completed install as a
failure.

## Root cause

`PathsEqual` catches one of the two exceptions `GetFullPath` raises:

```csharp
try
{
    return string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        comparison
    );
}
catch (ArgumentException)
{
    // A malformed PATH entry simply is not a match.
    return false;
}
```

`PathTooLongException` derives from `IOException`, not from `ArgumentException`,
so an over-long entry escapes. (`NotSupportedException` is the third member of the
triple that every other site in this branch catches — see `ZSchemeHome.IsBinDir`,
`ToolchainRegistry.ReadLinkTarget`, `ToolchainInstaller.FullPathOrNull`,
`LinkCommand.cs:34-38`.)

Line 14 is unguarded outright:

```csharp
var binDir = Path.GetFullPath(ZSchemeHome.GetBinDir(home));
```

which throws for the same reasons on an over-long `ZSCHEME_HOME` — closely
related to [[an-unparseable-zscheme-home-kills-every-zsup-and-zs-invocation]],
though this one is downstream of the commit point and the other is upstream of
everything.

Placement is what makes it matter. `WarnIfBinDirNotOnPath` runs at
`InstallCommand.cs:123`, after the toolchain is installed, after `installed
toolchain '...'` has been printed, and after the default has been recorded. The
sibling advisory in the same class documents that exact hazard for its own catch
(`ZsupDoctor.cs:121-127`):

> This runs after `zsup install` has committed and printed success, and neither
> InstallCommand nor SelfCommand catches it -- so an unhandled one turns a
> completed install into a bare exception line and a non-zero exit.

`WarnIfRuntimeMissing` took that lesson; `WarnIfBinDirNotOnPath` did not.

## Suggested fix direction

Widen the catch to the triple the rest of the codebase uses, and guard line 14 the
same way — an advisory that cannot answer should stay quiet rather than fail:

```csharp
catch (Exception e)
    when (e is ArgumentException or PathTooLongException or NotSupportedException)
{
    // A malformed or over-long PATH entry simply is not a match.
    return false;
}
```

For `binDir`, the cleanest shape is to compute it inside a `try` and return early
without warning if it cannot be resolved: a home path the OS will not parse is a
problem the *install* would already have reported.

Worth a test asserting `WarnIfBinDirNotOnPath` tolerates a `PATH` containing an
over-long entry. The method takes an explicit `home`, so the test needs no
environment mutation beyond `PATH` itself.
