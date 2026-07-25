# LibraryCompiler.ScanDependencies registers raw module names, re-arming a fixed bug

**Found by:** fixing the aspnet duplicate-module bug in `5a24a11`. This is the same defect
shape, in the second of the two dependency scanners. **Inert today** — see below — so it was
deliberately left unchanged rather than fixed blind.

**Affects:** `LibraryCompiler.ScanDependencies`
(`src/ZScheme.Compiler/Package/LibraryCompiler.cs:583-604`), i.e. `zs build` / `zs install` of
any package.

## Symptom

None today. The concern is regression-proofing: `5a24a11` fixed exactly this defect in
`Compilation`, where it caused all ten aspnet test files to fail with

```
Ambiguous overload of 'http/get'; candidates: http/http/http/get, http/http/get
```

because a module reachable under both its package alias (`http`) and its canonical name
(`http/http`) became two graph nodes, was compiled twice, and had every exported function
registered twice in the overload set (`TypeEnv.DefineImportedBinding` keys candidates as
`{moduleName}/{name}`).

## Root cause

`LibraryCompiler.cs:591` adds the import name to the graph verbatim, with no
`ModuleResolver.ResolveAlias` call:

```csharp
if (!localModules.ContainsKey(import.ModuleName))
    continue;
...
graph.AddModule(import.ModuleName);
graph.AddDependency(moduleName, import.ModuleName, import.Span);
```

Two things keep this harmless right now:

1. **It only tracks intra-package modules.** The `localModules.ContainsKey` guard at
   `LibraryCompiler.cs:583` drops anything not in the package's own source set, and those keys
   are already prefix-qualified (`http/http` for `src/http.zs` under prefix `http`).
2. **Its resolver never has aliases registered.** `LibraryCompiler.cs:346-357` calls
   `AddSearchPath`/`AddPackagePath` only — no `AddModuleAlias` — so `ResolveAlias` would be the
   identity function and adding the call would change nothing.

Fact (1) has a second consequence worth noting independently: an intra-package import written
via the package's own alias would silently *not* be tracked as a dependency at all, so the
topological order would not guarantee it is compiled first.

## Suggested fix direction

Do not just sprinkle in a `ResolveAlias` call — with no aliases registered it is dead code that
looks like protection. Better, in rough order of value:

1. Add a test that a package whose own module imports a sibling via the package prefix compiles
   in the right order, pinning behaviour (1) above.
2. If `LibraryCompiler` ever gains aliases (e.g. so intra-package imports may use the prefix
   form), canonicalize at the same time — and mirror the invariant comment now on
   `Compilation.ScanDependencies` in
   `src/ZScheme.Compiler/Pipeline/Compilation.DependencyResolution.cs`.
3. Longer term, the two scanners are near-duplicates with subtly different rules. Collapsing
   them onto one implementation would make this class of divergence impossible rather than
   merely documented.

## Priority note

Low: no current failure, and the fix is a no-op until preconditions change. Recorded because the
identical defect in the sibling scanner shipped broken for 209 commits before anything noticed —
the aspnet suite was the only consumer whose import shape reached it.
