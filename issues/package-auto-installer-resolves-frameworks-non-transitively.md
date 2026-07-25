# PackageAutoInstaller resolves only directly declared frameworks, not inherited ones

**Found by:** audit of `b7d1961`, which added framework resolution to `PackageAutoInstaller` in
the first place. Code inspection — **not reproduced.** Verified that no dependency closure is
available at that point in the method (a search for `closure` in the file finds nothing).

**Affects:** `PackageAutoInstaller` (`src/ZScheme.Compiler/Package/PackageAutoInstaller.cs:80-95`),
i.e. auto-install triggered from the language server and from `zs` when a dependency is not yet
cached.

## Symptom

A package that inherits a shared-framework dependency *transitively* — it does not declare
`(framework Microsoft.AspNetCore.App)` itself, but depends on a ZScheme package that does — is
auto-installed with no reference assemblies for that framework. Expect the same class of failure
`b7d1961` set out to fix: reflection over framework types failing during auto-install, and in
the language server that surfaced as a document with no diagnostics and no AST at all.

Not currently reachable from anything in-repo: `packages/aspnet` declares
`Microsoft.AspNetCore.App` directly, and no package in `packages/` inherits a framework without
declaring it. So this needs a new package shape to trigger.

## Root cause

`PackageAutoInstaller.cs:80-84` resolves the manifest's **own** framework list:

```csharp
if (manifest.Dependencies.Frameworks.Count > 0)
{
    var frameworkPaths = FrameworkResolver.Resolve(
        manifest.Dependencies.Frameworks,
        frameworkDiag
    );
```

`PackageBuilder` does both — the transitive closure *and* the manifest's own
(`src/ZScheme.Compiler/Package/PackageBuilder.cs:69` and `:87`):

```csharp
FrameworkResolver.Resolve(closure.Frameworks, diagnostics)     // :69  transitive
FrameworkResolver.Resolve(manifest.Dependencies.Frameworks, diagnostics)  // :87  own
```

`PackageAutoInstaller` has no `closure` in scope at that point, so this is not a one-line change:
it needs `PackageDependencyResolver.ResolveTransitiveClosure` to have run first, or the closure
threaded in from the caller.

## Suggested fix direction

1. Add a test package fixture that inherits a framework without declaring it, and assert
   auto-install produces the framework reference paths. That fixture is the missing piece —
   `tests/ZScheme.Compiler.Tests/Package/PackageAutoInstallerTests.cs` is the home for it.
2. Then resolve the closure in `PackageAutoInstaller` and union its `Frameworks` with the
   manifest's, matching `PackageBuilder.cs:69-89`.
3. While there, check the ordering of `assemblySearchPaths` against the other call sites — the
   three producers currently build it in three different orders, which has its own consequence
   (see
   [interop-load-context-leaks-a-context-per-search-path-ordering.md](interop-load-context-leaks-a-context-per-search-path-ordering.md)).

## Priority note

Medium-low. Unreachable with today's packages, but it is the remaining half of a bug that was
already reported once from the field, and the failure mode is the bad one — the language server
producing nothing at all for the file, with nothing logged.
