# `zs lint` dies with an unhandled `TypeLoadException` when one file's import graph conflicts with a host assembly version

**Found by:** running `zs lint --fix` per package during the 0.5 deprecation modernization
(PR #11). First reproduced in `packages/di` on Windows, .NET 10, toolchain `dev`
(worktree build `0.5.0+fe3c39c3`). **Confirmed** — deterministic; plain `zs lint` (no
`--fix`) crashes identically.

**Affects:** `zs lint` / `zs lint --fix` on any package whose *test* sources pull a package
with `import-clr` dependencies into the same process as a NuGet set carrying a different
version of one of the assemblies those types live in. Reproduced with `packages/di`
(test files import `zunit` → xunit.v3, while the package pins
`Microsoft.Extensions.DependencyInjection` 10.0.0). The main sources and `zs build` of the
same package are unaffected. A crashed `--fix` run leaves the files analyzed *before* the
crash already rewritten on disk and the rest of the run unrun.

**Workaround:** lint the sources in separate runs, per directory — `zs lint src` and then
`zs lint test` — or skip the crashing file. (In `di`, `zs lint src` is clean; only the test
files trigger the crash.) Note the crash still happens in a `--fix` run started that way;
the split runs just keep it from eating files after the crash.

## Symptom

```
$ cd packages/di
$ zs lint          # or: zs lint --fix
src\provider.zs: 1 fix applied        # (with --fix; the file is rewritten)
Unhandled exception. System.TypeLoadException: Could not load type 'Microsoft.Extensions.DependencyInjection.IKeyedServiceProvider' from assembly 'Microsoft.Extensions.DependencyInjection.Abstractions, Version=6.0.0.0, Culture=neutral, PublicKeyToken=adb9793829ddae60'.
   at System.Signature..ctor(IRuntimeMethodInfo methodHandle, RuntimeType declaringType)
   at System.Reflection.RuntimeMethodInfo.<get_Signature>g__LazyCreateSignature|25_0()
   at System.Reflection.RuntimeMethodInfo.FetchNonReturnParameters()
   at System.Reflection.RuntimeMethodInfo.GetParameters()
   at ZScheme.Compiler.Codegen.ClrInterop.ArgTypesMatchParams(MethodInfo candidate, IReadOnlyList`1 argTypes, SourceSpan span)
   at ZScheme.Compiler.Codegen.ClrInterop.SelectOverload(List`1 candidates, ...)
   at ZScheme.Compiler.Codegen.ClrInterop.ResolveOverloadCallSite(String typeName, String methodName, ...)
   at ZScheme.Ir.IrLowering.LowerApply(Apply n)
   ...
   at ZScheme.Compiler.Pipeline.Compilation.CompileModule(...)
   at ZScheme.Compiler.Pipeline.Compilation.CompileResolveAndCompileImports(...)
   at ZScheme.Cli.LintCommand.Analyze(LintContext context, String file, String source)
   at ZScheme.Cli.LintCommand.Run(String[] args)
```

The crash is inside `Module zunit/zunit -> di/provider -> di-abstractions/services` — the
inline compilation of `di/provider` (an import of `di/test/di-tests.zs`), at the first
apply in its body, `clr-build-provider`, i.e. the overload resolution of
`ServiceCollectionContainerBuilderExtensions/BuildServiceProvider`. The last debug line
before the throw is `LowerImportClr: after processing, _clrImports count=1,
keys=[clr-build-provider]`.

Isolating the trigger (all in `packages/di`, each a fresh process):

| Command | Result |
| --- | --- |
| `zs lint src` | clean, exit 0 |
| `zs lint test` | same `TypeLoadException` |
| `zs lint` / `zs lint --fix` | crashes after rewriting `src/provider.zs` |
| `zs build` | succeeds |

So the crash needs the *test* import graph (zunit → xunit.v3 assemblies) loaded into the
same process as the DI 10.0.0.0 set.

## Root cause

The failing step is `MethodInfo.GetParameters()` on a candidate inside
`ClrInterop.SelectOverload`. Materializing that signature makes the runtime load
`IKeyedServiceProvider` (added in DI.Abstractions 8.0) **from assembly
`Microsoft.Extensions.DependencyInjection.Abstractions, Version=6.0.0.0`** — and 6.0 does not
contain the type, hence the `TypeLoadException`.

This is the exact failure class `InteropLoadContext`'s doc comment describes for
`zs-lsp` — a host copy of `Microsoft.Extensions.DependencyInjection.Abstractions` 6.0 (there,
pulled in by OmniSharp) making `MethodInfo.GetParameters()` throw when reflecting over an
assembly built against 10.0. The context split was designed to keep that from happening in
the private context, and `IsClrAssignable` absorbs what gets through *after* the signature
is materialized. `GetParameters()` happens *before* any of that can help: it is the
runtime's own type resolution, and it throws rather than returning a mismatched signature.
`SelectOverload` iterates candidates in a `Where` predicate and nothing in the chain
(`ArgTypesMatchParams`, `DescribeCandidateForLog`, the tie-break ordering) anticipates a
candidate whose signature cannot be materialized in this process.

What is not yet established (the open part):

- Where the 6.0.0.0 identity comes from in a `zs` CLI process. It is *not* in the CLI's
  own dependency closure (`zs.deps.json` carries no `Microsoft.Extensions.DependencyInjection*`;
  only `zs-lsp.deps.json` does, via `OmniSharp.Extensions.LanguageServer` 0.19.9). It is not
  in any `~/.zscheme/cache/nuget/<set>/bin` directory — all 17 cached copies of
  `Microsoft.Extensions.DependencyInjection.Abstractions.dll` on this machine are 10.0.0.0.
  The resolved set for the failing compilation (5 packages incl. xunit.v3 3.2.2) was
  checked: the xunit assemblies do not reference the Abstractions assembly at all, and the
  10.0.0.0 container references Abstractions 10.0.0.0. So the 6.0.0.0 AssemblyRef — or
  however the runtime is handed that identity — has not been located yet. Prime suspects for
  the next step of the investigation: the global `~/.nuget/packages` and the SDK fallback
  folders (which the default context *can* probe, unlike the private context), and the
  default-context `Resolving` handler `ClrInterop` attaches. A run with
  `AssemblyLoadContext` tracing, or a log of `AppDomain.CurrentDomain.GetAssemblies()` plus
  the failing method's `DeclaringType.Assembly` identity immediately before the throw,
  would settle it.
- Why the *build* path never hits the same resolution: `zs build` of `di` compiles only the
  main sources (no zunit, no xunit assemblies loaded) and succeeds. If the trigger is the
  co-presence of the xunit set in the private context, a `di` *test build*
  (`zs test` in `packages/di`) should reproduce the same split and may crash or mis-resolve
  the same overload — unverified.

## Suggested fix direction

Two independent layers, worth doing both:

1. **No candidate may take down the process.** In `ArgTypesMatchParams` (and the sibling
   `GetParameters()` call sites in the same path — `DescribeCandidateForLog` and the
   ordinal tie-break in `SelectOverload`), catch `TypeLoadException` /
   `FileNotFoundException` / `ReflectionTypeLoadException` around the signature
   materialization and treat the candidate as non-matching, logging at debug with the
   candidate's `DeclaringType.Assembly` identity and the load context. A candidate whose
   signature cannot be read in this process is not a match; today it is indistinguishable
   from a compiler crash. The existing "no candidate accepted" fallback (backend's own
   reflection / string-based emission) then runs as designed.
2. **One file's failure must not eat the run.** `LintCommand.Run` already has a
   "could not be analyzed" bucket for files that fail type-checking; an unexpected
   exception from `Analyze` should land in that same bucket (message + exit 1) rather than
   propagate out of `Main`. And for `--fix` specifically, consider analyzing *every* file
   first and writing *after* — today the rewrite is per-file, so a crash on file N+1
   leaves files 1..N rewritten and the summary never printed, which reads as "half done,
   state unknown" rather than "failed, and here is what changed."

The root identity hunt (the open part of the root cause) should be filed as follow-up
work on `InteropLoadContext`: until it is pinned, direction 1 is what keeps this class of
bug — of which OmniSharp's 6.0 in `zs-lsp` is one documented instance — from being a
process death.

## Priority note

Medium. It is a crash, not a miscompile: the build path is untouched, the fixes applied
before the crash were verified-correct single-token rewrites, and a workaround exists
(split runs per directory). But `zs lint --fix` is the documented way to modernize a
package, it is unusable on `di` today, and the half-rewritten state of a crashed `--fix`
run is the kind of thing that erodes trust in the tool. Should be fixed before more
packages go through the sweep, or at least before the sweep reaches any package whose test
graph loads a version-conflicting set.
