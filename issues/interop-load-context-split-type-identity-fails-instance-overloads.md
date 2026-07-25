# Types can resolve from two load contexts, silently failing `:instance` overload selection

**Found by:** audit of `b7d1961` while fixing the unrelated aspnet duplicate-module bug. Code
inspection only — **not reproduced with a failing test.** The mechanism (two contexts populated
from the same search paths) is verified; the resulting mis-resolution is not.

**Affects:** `ClrInterop.SelectOverload` and everything downstream — every `import-clr :instance`
binding, plus `PairwiseSpecificity` and `MapClrTypeToZType`. The stdlib collection modules
(`list.zs`, `vector.zs`, `map.zs`) are built entirely on `import-clr :instance`, so the blast
radius is wide even though no concrete failure has been pinned yet.

## Symptom

An `:instance` overload silently fails to resolve: `ResolveInstanceOverloadCallSite` returns
null with **no diagnostic**, and the backend falls back to its own reflection, producing either
a wrong overload or a hard emit failure with no useful message.

The silence is deliberate but unhelpful here — `ClrInterop.cs:202` passes
`reportAmbiguity: false` for the instance path, so the "no candidate matched" branch at
`ClrInterop.cs:276` never reports.

## Root cause

Before `b7d1961` all reflection went through `AssemblyLoadContext.Default`, so type identity was
at least *consistent*. Now assemblies can be populated into **two** contexts from the same
search paths:

- the private context, via `InteropLoadContext.Load`/`LoadFromPath`;
- **and the default context**, because `ClrInterop`'s own `Resolving` handler is still
  registered there and loads from the same `_searchPaths`
  (`src/ZScheme.Compiler/Codegen/ClrInterop.cs:36-61`, hooked up at `:61`):

  ```csharp
  AssemblyLoadContext.Default.Resolving += _resolveHandler;   // handler calls context.LoadFromAssemblyPath
  ```

  `IlEmitter.cs:284` also uses `Assembly.LoadFrom`, which targets the default context.

Because `FindType` resolves through `AppDomain.CurrentDomain.GetAssemblies()` — which spans all
contexts — it can hand back a `Type` from the private context for one name and a `Type` from the
default context for another. **`Type` objects from different load contexts are never
reference-equal, and `IsAssignableFrom` is always false between them**, even for byte-identical
assemblies.

That breaks the comparison at the heart of overload matching. `ArgBindsToParam`
(`ClrInterop.cs:345`) resolves the argument type via `ResolveZLeafToClr` → `FindType` and
compares it to `param.ParameterType` (which came from the receiver's context) using
`IsClrAssignable`, whose `Type.IsAssignableFrom` and reference-equality tests both fail. Every
candidate is rejected, `matches.Count == 0`, and `SelectOverload` returns null.

Only the name-based fallback in `ArgBindsToParam` — `MapClrTypeToZType(paramType)` yielding a
`ZNamedType` compared by `FullName` — can rescue this, and only when the ZType name happens to
match the CLR full name.

## Suggested fix direction

Pick one context per compilation and resolve *everything* through it:

1. Stop registering the `Resolving` handler on `AssemblyLoadContext.Default`
   (`ClrInterop.cs:61`); route those loads into `_loadContext` instead. Note `Dispose` at
   `:66` unregisters it, so ownership is already tracked.
2. Audit `IlEmitter.cs:284`'s `Assembly.LoadFrom` for the same reason.
3. Have `FindType`/`FindTypeForMember` prefer `_loadContext.Assemblies` (shared with
   [interop-load-context-host-assembly-preempts-private-load.md](interop-load-context-host-assembly-preempts-private-load.md)).
4. Independently: consider whether `reportAmbiguity: false` at `ClrInterop.cs:202` should at
   least emit a debug log, so a resolution failure of this kind stops being invisible.

## Historical note

This is a *re-introduction*, by a different mechanism, of the class of bug fixed in `738c153`
("Fix import-clr validation being contaminated by process-wide reflection scan"). The comment
at `src/ZScheme.Compiler/Types/TypeInferer.cs:1941` still points at that issue file, which was
deleted when it was fixed — a dangling reference worth cleaning up alongside this.

## Priority note

Highest-severity of the three `InteropLoadContext` findings if it fires, because it fails
silently. But note the whole test suite, all seven package suites and all 123 examples pass, so
either it is not currently firing or the `FullName` fallback is absorbing it. Do not change
load behaviour without a reproduction.
