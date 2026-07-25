# InteropLoadContext is bypassed when the host already has the assembly loaded

**Found by:** audit of `b7d1961` ("Make package compilation independent of the hosting
process's assemblies") while fixing the unrelated aspnet duplicate-module bug. Code
inspection only — **not reproduced with a failing test.**

**Affects:** `ClrInterop.EnsureAssemblyLoaded`
(`src/ZScheme.Compiler/Codegen/ClrInterop.cs:1300`) and every `import-clr … :from "Assembly"`
binding that names an assembly the hosting process also ships. The scenario `b7d1961`'s
commit message describes — `zs-lsp` carrying
`Microsoft.Extensions.DependencyInjection.Abstractions` 6.0 via OmniSharp while a package is
built against 10.0 — is still reachable.

## Symptom

`b7d1961` moved assembly *loading* into a private `InteropLoadContext`, but reflection still
resolves types through lookups that see every load context and answer first-loaded-wins. When
the host's copy is already present, nothing is loaded privately at all, so the host's version
is what gets reflected. Depending on how far the version skew goes this shows up as
`MethodInfo.GetParameters()` throwing `FileNotFoundException` (the original failure), or as
silently reflecting the wrong assembly's members.

## Root cause

Two halves that do not line up.

`EnsureAssemblyLoaded` short-circuits on *any* loaded assembly of that simple name, in any
context, at any version (`ClrInterop.cs:1300-1310`):

```csharp
foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
    if (string.Equals(loaded.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
        return;   // never reaches _loadContext.LoadByName below
```

`AppDomain.CurrentDomain.GetAssemblies()` enumerates across all load contexts, so the host's
6.0 copy satisfies this test and the private load never happens.

The lookups in `FindType` then cannot prefer the private context even when it *is* populated:

- `ClrInterop.cs:1374` — `Type.GetType(typeName)` resolves only against the calling
  assembly's context, i.e. the default one.
- `ClrInterop.cs:1365`, `:1379`, `:1436` — linear scans of
  `AppDomain.CurrentDomain.GetAssemblies()`, first match wins, in load order. The host's
  assemblies are loaded at process startup, long before any compile.

So `b7d1961` only fixed the sub-case where the wanted assembly is *not* already loaded and
`ProbeDirectory` (`ClrInterop.cs:1466`) is the path that finds it.

## Suggested fix direction

Make the private context authoritative for lookup, not just for loading:

1. Narrow the early return in `EnsureAssemblyLoaded` to consider only assemblies already in
   `_loadContext.Assemblies`, so a host-loaded copy no longer suppresses the private load.
2. Give `FindType`/`FindTypeForMember` a first pass over `_loadContext.Assemblies` before
   falling back to `Type.GetType` and the `AppDomain` scan.

Both steps change which `Type` object comes back, which is exactly the hazard described in
[interop-load-context-split-type-identity-fails-instance-overloads.md](interop-load-context-split-type-identity-fails-instance-overloads.md)
— fix the two together, and land a reproduction first.

## Reproducing this first

`tests/ZScheme.Compiler.Tests/Codegen/InteropLoadContextTests.cs` now emits real assemblies
onto a search path, which is the scaffolding this needs. A reproduction wants a package built
against a *newer* version of an assembly the test host itself carries, then an
`import-clr … :from` over a member that only exists in the newer one.

## Priority note

This is the headline defect of the three, because it means the commit's stated goal is not yet
met. Practical exposure is limited to `zs-lsp` (the `zs` CLI pre-loads nothing conflicting),
but that is precisely where it was originally reported.
