# Locate where the `6.0.0.0` DI Abstractions identity enters the default `AssemblyLoadContext` in a `zs` CLI process

**Found by:** follow-up to
[`lint-dies-with-typeloadexception-when-a-files-import-graph-conflicts-with-a-host-assembly.md`](lint-dies-with-typeloadexception-when-a-files-import-graph-conflicts-with-a-host-assembly.md)
— its *Still open* part 1 and its note that the root-identity hunt "should be filed as
follow-up work on `InteropLoadContext`". The crash that issue documents is contained:
`ClrInterop.TryGetParameters` now treats an unreadable candidate signature as a non-match, and
`ClrInteropTests.OverloadResolution_UnreadableSignature_ReturnsNullInsteadOfCrashing` pins the
containment. But the assembly identity that made the signature unreadable has never been
located, and until it is, that guard is what keeps this class of bug from being a process
death.

**Affects:** diagnosis, not behavior — no crash or miscompile is known to survive the
`TryGetParameters` guard, and `zs lint` in `packages/di` now completes and reports its hints.
It matters because until the `6.0.0.0` identity is pinned, the private `InteropLoadContext`
split cannot be verified to be load-bearing, and every future host-assembly version conflict
(the `zs-lsp`/OmniSharp 6.0 case is the documented instance) will be diagnosed by elimination
rather than by knowing what the default context is handing the runtime.

## What is already established

All from the original issue's *Root cause* section, each verified on the reproducing machine
(Windows, .NET 10):

- The failing step is `MethodInfo.GetParameters()` on an overload candidate inside
  `ClrInterop.SelectOverload`: materializing the signature makes the runtime load
  `Microsoft.Extensions.DependencyInjection.IKeyedServiceProvider` (added in DI.Abstractions
  8.0) from assembly `Microsoft.Extensions.DependencyInjection.Abstractions, Version=6.0.0.0`,
  which does not contain it — hence the `TypeLoadException`.
- The identity is **not** in the CLI's own dependency closure: `zs.deps.json` carries no
  `Microsoft.Extensions.DependencyInjection*` at all (only `zs-lsp.deps.json` does, via
  `OmniSharp.Extensions.LanguageServer` 0.19.9).
- It is **not** in any `~/.zscheme/cache/nuget/<set>/bin` directory: all 17 cached copies of
  `Microsoft.Extensions.DependencyInjection.Abstractions.dll` on the machine are 10.0.0.0.
- It is **not** referenced by the xunit assemblies: the resolved set for the failing
  compilation (5 packages incl. xunit.v3 3.2.2) was checked — the xunit assemblies do not
  reference the Abstractions assembly at all, and the 10.0.0.0 container references
  Abstractions 10.0.0.0.
- The `zs build` path never hits the same resolution (it compiles only the main sources, no
  xunit assemblies loaded), and `zs lint src` in `packages/di` is clean; the trigger is the
  *test* import graph (zunit → xunit.v3) co-resident with the DI 10.0.0.0 set.

## What is not yet established (the work)

Where the `6.0.0.0` `AssemblyRef` — or however the runtime is handed that identity — comes
from in a `zs` CLI process. Prime suspects, in the order they are worth checking:

1. The global `~/.nuget/packages` — the default `AssemblyLoadContext` *can* probe it (via the
   SDK's default probe set), unlike the private `InteropLoadContext`, and a `6.0.0.0`
   Abstractions from an older transitive closure could live there.
2. The SDK fallback folders — the same default-context probe set, same reasoning.
3. The default-context `Resolving` handler `ClrInterop` attaches
   ([`ClrInterop.cs`](../src/ZScheme.Compiler/Codegen/ClrInterop.cs), constructor): it resolves
   by simple name from the compilation's assembly search paths and loads the match *into the
   default context* — if a search path ever carries a `6.0.0.0` copy, this is how it would
   enter the default context by name.

Suggested evidence-gathering (any one of these should settle it):

- An `AssemblyLoadContext` tracing run of `zs lint test` in `packages/di` (the
  `AssemblyLoadContext` trace source, e.g. via `dotnet-trace`), filtered on
  `Microsoft.Extensions.DependencyInjection.Abstractions` — which context asks for `6.0.0.0`
  and what is handed to it.
- A temporary log in the `TryGetParameters` catch (the natural observation point, right before
  the throw is absorbed): the `DeclaringType.Assembly` identity of the failing method plus a
  dump of `AppDomain.CurrentDomain.GetAssemblies()` — the assembly whose *reference* to
  Abstractions 6.0.0.0 is driving the resolution is the thread to pull.

Deliverable: a note here (or on `InteropLoadContext`) naming the exact assembly, file, and
load context that supplies the `6.0.0.0` identity, with the evidence that pins it. If it turns
out the default context is legitimately probing a machine-global folder for managed
references, the follow-on question is whether the private-context isolation should extend to
the signature materialization itself (force `GetParameters()` to resolve in the private
context) rather than relying on `TryGetParameters` to absorb the throw.

## Also unverified: does `zs test` in `packages/di` reproduce the same split?

The build path succeeds because it loads no xunit assemblies. If the trigger is the
co-presence of the xunit set in the private context, a *test* build (`zs test` in
`packages/di`) should load the same split — and either hit the same overload
(`ServiceCollectionContainerBuilderExtensions/BuildServiceProvider`) or mis-resolve it.
Unverified. If it reproduces, the bug is not lint-specific and the `TryGetParameters` guard —
which sits on the shared `SelectOverload` path — is doing more work than the original issue
credits; if it does not, the lint context's assembly search paths are part of the trigger and
worth recording.

## Priority note

Low. No user-visible defect survives the guard: `zs lint` in `packages/di` completes, the
regression test pins the containment, and the `--fix` half-rewrite hazard the original issue
worried about is gone (lint now analyzes every file before writing any). Worth doing before
the guard is ever questioned or removed: it is the difference between knowing the
private-context design works and trusting that it does.
