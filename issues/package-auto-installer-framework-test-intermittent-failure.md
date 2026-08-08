# Intermittent: PackageAutoInstallerTests.FrameworkInheritedFromADependencyIsResolvedForAutoInstall

**Status: not diagnosed.** Observed once, never reproduced. Everything below the
Symptom section is unverified suspicion recorded so the next occurrence starts
from something rather than nothing.

## Symptom

`ZScheme.Compiler.Tests.Package.PackageAutoInstallerTests.FrameworkInheritedFromADependencyIsResolvedForAutoInstall`
failed on one full-solution `dotnet test ZScheme.slnx` run
(2352 passed / 2353). It passed on every run before and after.

The test plants two temp packages — `zs-test-fw-provider`, which declares
`(dependencies (framework Microsoft.AspNetCore.App))`, and `zs-test-fw-consumer`,
which depends on the provider and `import-clr`s
`Microsoft.AspNetCore.Http.HttpMethods/IsGet` — then asserts
`PackageAutoInstaller.TryAutoInstall` resolves the inherited framework so the
consumer compiles.

**The failure detail was not captured** — only the `[FAIL]` line from filtered
output. The assertion message and diagnostics are unknown, which is the single
biggest gap here. Capture full output on any recurrence.

## When it happened

Immediately after commit `92d6dc2` ("Canonicalize CLR type names so short and
qualified spellings unify"), on the first full test run following that commit's
pre-commit CSharpier reformat and the rebuild it triggered.

Runs since, all clean:

- 1× isolated (`--filter FullyQualifiedName~FrameworkInheritedFromADependency...`)
- 1× full solution (`dotnet test ZScheme.slnx`)
- 3× full `ZScheme.Compiler.Tests` suite

It was also not seen during several full-suite runs while `92d6dc2` was being
developed, before it was committed.

## Suspicions (unverified — do not treat as findings)

1. **`ClrInterop` churn on `AssemblyLoadContext.Default.Resolving`, new in
   `92d6dc2`.** `TypeNameCanonicalizer.Resolve` constructs a short-lived
   `ClrInterop` on every cache miss; each one subscribes a `Resolving` handler
   on the default context in its constructor and unsubscribes on dispose. That
   handler loads assemblies found on *its* compilation's search paths into the
   default context, where they stay for the life of the process. This test is
   one of the few that depends on framework assembly resolution
   (`Microsoft.AspNetCore.App`) rather than just the BCL, so it is unusually
   exposed to whatever is already resident in the default context. Under xUnit
   parallelism, more concurrent subscribe/unsubscribe churn means more chances
   for an assembly to be pulled in from an unexpected path.

   Counterpoint worth checking: `Unifier.IsClrSubtype` already constructed an
   undisposed `ClrInterop` per call long before this commit, so the pattern is
   not new — only its frequency changed, and arguably not by much.

2. **Pre-existing flakiness in framework/NuGet resolution under parallel test
   execution.** The test does real filesystem work (temp anchor dir, temp cache,
   package install) and probes framework reference packs. A race there would be
   independent of `92d6dc2`. No baseline data either way.

## Ruled out

**Not** the `MetadataSerializer.FormatVersion` 1 → 2 bump in `92d6dc2`. That
bump did cause exactly one self-healing failure in the same session —
`ZScheme.LanguageServer.Tests.TypeDefinitionTests.ImportedRecordType_JumpsAcrossFiles`,
via stale metadata under the shared `~/.zscheme` cache being rejected once and
rebuilt — but this test is isolated from that: `PackageAutoInstallerTests`
creates its own `CacheDir` under a per-instance temp directory and passes it
explicitly. The two failures were initially conflated; they are unrelated.

## What would settle it

- Capture the full failure output (assertion text + `DiagnosticBag` contents) on
  recurrence. Nothing else is worth much without this.
- Run the suite repeatedly at `02f6e53` (the parent commit) to establish whether
  a baseline flake rate exists at all.
- Re-run with xUnit parallelism disabled to test suspicion 2.
- Instrument `ClrInterop` construction/disposal counts and which assemblies its
  `Resolving` handler pulls into the default context during a full run, to test
  suspicion 1.
