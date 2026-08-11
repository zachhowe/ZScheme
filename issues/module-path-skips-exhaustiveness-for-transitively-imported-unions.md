# A non-exhaustive match over a transitively-imported union is an error in a primary unit and silently accepted in a module

**Found by:** investigating the match-arm-binder-lost-across-an-await bug in ZWorld's script
package, fixed by routing both compile paths through `RegisterImportedTypeMetadata`
(`Pipeline/Compilation.cs:1006`). That fix closed the *pattern field-type* half of the module
path's narrow import scope. This is the other half, left open deliberately — see "Why this was not
fixed alongside".

**Affects:** every module that matches on a union it did not import by name — which is the normal
way to consume `Option`/`Result`, since a `(Some v)` pattern over the return of an imported
function does not require importing `stdlib/option`.

## Symptom

The same match, missing the same case, compiled two ways. As the primary unit:

```scheme
(module main)
(import stdlib/mutable/hash)

(define (peek [h : (Mutable-Hash String Int)]) : Int
  (match (hash-ref h "a")
    [(Some v) v]))
```

```
$ zs compile main.zs --backend il --package-path <zscheme>/packages/stdlib
Error: Non-exhaustive match: missing cases None at main.zs(5:3)
```

Move the identical function into `helper.zs`, import it from `main.zs`, and compile again with
`--module-path .`:

```
$ zs compile main.zs --backend il --module-path . --package-path <zscheme>/packages/stdlib
Generated: output.dll
```

No error, no warning. A `None` at runtime hits whatever the backend emits for a fallthrough.

## Root cause

`ExhaustivenessValidator` is handed union declarations from the module's **direct** imports only
(`Pipeline/Compilation.ModuleCompilation.cs:211-214`):

```csharp
new ExhaustivenessValidator(modDiag).Validate(
    program,
    transModules.SelectMany(m => m.ExportedIrDefinitions.OfType<IrNode.UnionDecl>())
);
```

`transModules` is built from the module's own `(import ...)` forms (`:113-132`). `helper.zs`
imports `stdlib/mutable/hash`; `Option` is declared in `stdlib/option`, which *hash* imports, so no
`Option` `UnionDecl` reaches the validator. An unregistered union is not checked — the checker has
no case list to compare the arms against, so it is permissive rather than noisy.

The whole-program path has no such gap (`Pipeline/Compilation.cs:263-266`): it passes
`compiledModules`, which `CompileResolveAndCompileImports` has already stuffed with every module in
`_moduleCache` — "direct imports + transitive deps" (`:712-714`).

This is the same shape as the three module-path gaps already fixed this cycle (`TailCallLowering`,
ZS0005, and the pattern field types), and the same shape as their fixes: give the module path the
closure the whole-program path already uses.

## Suggested fix direction

`_moduleCache.Values` at that point in `CompileModule` is this module's fully-resolved dependency
closure (every import was recursed through at `:123-132`), which is exactly what the pattern
metadata fix now uses twelve lines below. The mechanical change is to source the validator's union
set from the same place.

Do not do that blind. Widening exhaustiveness scope can only *add* diagnostics, and any match that
has been silently unchecked since this gap appeared will start reporting. Before changing it:

1. Make the change locally and run `run-package-tests.ps1`, `run-package-csharp-tests.ps1`,
   `build-examples.ps1`, and ZWorld's `run-scripts-tests.ps1` to enumerate what starts failing.
2. Each new diagnostic is either a real missing case (fix the source) or a checker limitation
   (needs its own fix first). The count decides whether this lands as one commit or a series.

## Also worth folding in

`PatternResolver.AnnotatePattern` (`Ir/PatternResolver.cs`) now logs a Serilog **warning** when a
constructor pattern's union does not resolve, which is a broken invariant for a type-checked
program — the class doc has always claimed "no unresolved constructor pattern reaches the
emitters". It is debug-sink only on purpose: which patterns can legitimately fail to resolve (bare
type-var scrutinees, generic contexts) has not been established, so it must not fail a build until
it has. Establishing that set and promoting the warning to a real diagnostic belongs with this
issue — both are about the compiler noticing that its pattern metadata is incomplete instead of
quietly emitting worse code.

## Checked and *not* a bug

The type-alias pre-pass (`ModuleCompilation.cs:165-172`) reads the same direct-imports-only
`transModules`, so it looks like a third instance of this family. It is not: type aliases live in
the compilation-wide `TypeAliases` registry, which is seeded independently
(`RegisterPreludeTypeAliases`, and `LibraryCompiler`'s `MergeFrom`). Verified with a local chain —
`base` declares `(define-type-alias (Bag ^a) …)`, `mid` imports `base`, `user` imports only `mid`
and uses `(Bag Int)` in a signature — which compiles clean through `--module-path`. No need to
re-investigate it.

## Priority note

Below a miscompilation, above ordinary polish. Nothing is emitted *wrongly*; a real error is simply
not reported, so the failure surfaces at runtime as an unmatched case in code the author believes
the checker vetted. It is worse than an ordinary missing check because it is **path-dependent**:
the same source is rejected in one file and accepted in another, so a match that was proven
exhaustive during prototyping loses that guarantee the moment it moves into a library module. The
package/library build path routes every module through here, so every `.zs` file in every package —
stdlib included — is currently unchecked wherever it matches on a transitively-imported union.
