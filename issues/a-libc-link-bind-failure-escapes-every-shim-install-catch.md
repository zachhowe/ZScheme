# A `libc` bind failure in the shim installer escapes every catch guarding it

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted.

**Affects:** `ShimInstaller.Link`
(`src/ZScheme.Toolchain/ShimInstaller.cs:225-231`), reached from `Build` on every
Unix `zsup install` and `zsup self update`.

## Symptom

On a Unix host where the `link` symbol cannot be bound — a musl or otherwise
non-glibc image where `libc` does not resolve under that name, a hardened
container with a stripped libc — `zsup install 0.4.0` prints
`installed toolchain '0.4.0'`, then dies with a bare unhandled-exception line.
The toolchain is installed, the shims are not, and none of the per-name reporting
built for exactly this situation runs.

## Root cause

`Link` is a P/Invoke:

```csharp
[LibraryImport("libc", EntryPoint = "link", SetLastError = true, ...)]
private static partial int Link(string existing, string created);
```

A failure to load the library raises `DllNotFoundException`; a failure to find the
entry point raises `EntryPointNotFoundException`. Both derive from
`TypeLoadException`/`SystemException`, so neither is an `IOException` or an
`UnauthorizedAccessException` — and every catch on the path filters on exactly
those two:

- `ShimInstaller.Install:89` — `catch (Exception e) when (e is IOException or UnauthorizedAccessException)`,
  the per-name catch whose whole purpose is to turn one shim's failure into a
  `Failure` entry rather than an abort;
- `InstallCommand.StampShims:253` — same filter;
- `SelfCommand`'s stamping — same filter;
- `InstallCommand.Run`'s big filter at `:69-88`, which lists nine exception types
  and neither of these.

So the exception unwinds past all of them to `Main`, which has no handler
(`Program.cs`), after the install has committed and printed success.

The class remarks state the contract this breaks
(`ShimInstaller.cs:67-73`): every name is attempted even when an earlier one
fails, because stopping early leaves the mixed-version state the class exists to
rule out. A `DllNotFoundException` on the first name stops it before the second.

## Suggested fix direction

Bind failures are per-process, not per-name, so wrapping the call is better than
widening `Install`'s filter — it keeps the "IOException means this one shim" shape
intact and makes the fallback do what it was written for:

```csharp
private static bool TryLink(string existing, string created)
{
    try
    {
        return Link(existing, created) == 0;
    }
    catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
    {
        // No usable link(2) on this image; the symlink fallback below is the answer.
        return false;
    }
}
```

`Build` then falls through to the symlink path it already has for "a filesystem
without hardlinks", which is the correct behaviour for "no `link` at all" too.

Note this interacts with
[[the-shim-symlink-fallback-can-leave-a-dangling-zs]] — the fallback becomes
reachable in more cases once this is fixed, so the dangling-symlink check there is
worth doing at the same time.
