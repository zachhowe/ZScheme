# InteropLoadContext leaks a whole context per search-path ordering

**Found by:** audit of `b7d1961`. The non-collectibility is **verified** — the new
`InteropLoadContextTests` cannot delete their temp directories on teardown because loaded
assembly files stay mapped for the life of the process, which is why
`InteropLoadContextTests.TryDelete` exists. The multi-context accumulation is code inspection.

**Affects:** long-lived hosts, i.e. `zs-lsp`, which re-analyses on nearly every keystroke.
Memory grows without bound and never comes back.

## Symptom

A single language-server process accumulates several `InteropLoadContext` instances, each
holding its own copy of every target assembly, none of which can ever be unloaded.

The secondary effect originally recorded here — each new context adding another same-named
`Type` to `AppDomain.CurrentDomain.GetAssemblies()`, making `FindType`'s first-match-wins scan
depend on *which document was analysed first in the session* — has since been addressed:
`FindType` now scans its own `_loadContext.Assemblies` first, and each `ClrInterop` holds exactly
one context, so a sibling context's copies no longer preempt it. **The leak itself is unchanged.**

## Root cause

Two compounding facts.

**The cache key is the ordered list** (`src/ZScheme.Compiler/Codegen/InteropLoadContext.cs:78-81`):

```csharp
var key = string.Join("\0", searchPaths);
return Cache.GetOrAdd(key, _ => new InteropLoadContext([.. searchPaths]));
```

and the callers assemble that list in **different orders** for the same logical set:

- `src/ZScheme.LanguageServer/Analysis/AnalysisService.cs` — NuGet dir, then framework dirs
- `src/ZScheme.Compiler/Package/PackageBuilder.cs:69-89` — closure frameworks, closure ref
  paths, own frameworks, NuGet dir last
- `src/ZScheme.Compiler/Package/PackageAutoInstaller.cs:72-96` — NuGet dir, frameworks,
  manifest ref paths

So equivalent path sets miss the cache and mint a new context.

**Contexts are not collectible** (`InteropLoadContext.cs:73`):

```csharp
: base("ZSchemeClrInterop")     // no isCollectible: true
```

Nothing can ever be unloaded, and every context shares the same name, so diagnostics cannot
tell them apart.

Related: `VersionCache` (`InteropLoadContext.cs:66`) is keyed on path with no invalidation, so a
NuGet restore or a rebuild mid-session leaves permanently stale version data.

## Suggested fix direction

Do **not** simply sort the cache key. `Probe` walks `_searchPaths` in order and the first
exact-version match wins (`InteropLoadContext.cs:138`+), so path order is load-bearing —
conflating two orderings would silently change which assembly resolves. Either:

- **Normalise the producers** so every call site builds the list in one documented order, then
  the existing key naturally collides; or
- **Make ordering genuinely irrelevant** (e.g. resolve strictly by highest version, never by
  path priority), *then* sort the key.

Independently, pass `isCollectible: true` and give each context a distinguishing name, so a
context can be dropped when its search-path set goes out of use. Note collectible contexts
constrain what may be kept alive across their lifetime, so this needs care.

## Priority note

No correctness failure observed; this is a resource leak plus an order-dependence hazard. It
matters most for `zs-lsp` in a long editing session. The `FindType` order-dependence overlaps
[interop-load-context-split-type-identity-fails-instance-overloads.md](interop-load-context-split-type-identity-fails-instance-overloads.md)
— if lookups are made to prefer a single context, this becomes much less dangerous.
