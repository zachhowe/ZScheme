# `ExhaustivenessChecker` is never wired into the pipeline — non-exhaustive `match` compiles clean and throws at runtime

**Found by:** manual audit while evaluating .NET 11 union types (2026-07-12).

**Affects:** every `match` expression in every ZScheme program. This is a
**user-facing language bug**, not an internal-only defect: the compiler silently
accepts a program it is documented to reject, and the failure surfaces as a
runtime crash in the user's shipped binary.

**Severity:** high. `README.md:16` advertises "Pattern matching — Destructuring
with **exhaustiveness checking**" and `docs/SYNTAX-FORMS.md:123` states "The
compiler checks for exhaustiveness." Neither is true today.

## Symptom

A `match` that omits a union case produces **zero diagnostics**. It compiles
successfully, and then throws `System.InvalidOperationException: Non-exhaustive
match` when the missing case is reached at runtime.

Repro (`nonexh.zs`):

```scheme
(module nonexh)

(define-union Shape
  (Circle [radius : Int])
  (Rect [w : Int] [h : Int])
  (Tri [b : Int] [h : Int]))

;; NON-EXHAUSTIVE: `Tri` is never handled.
(define (area [s : Shape]) : Int
  (match s
    [(Circle r) (* r r)]
    [(Rect w h) (* w h)]))

(define (main) : Int
  (area (Tri 3 4)))
```

```console
$ dotnet run --project src/ZScheme.Cli -- compile nonexh.zs -o nonexh.dll
Generated: nonexh.cs
Generated: nonexh.csproj          # <-- no error, no warning

$ dotnet build nonexh.csproj      # builds clean

$ dotnet bin/Debug/net10.0/nonexh.dll
Unhandled exception. System.InvalidOperationException: Non-exhaustive match
   at ZSchemeGenerated.NonexhModule.Area(Shape s) in nonexh.cs:line 17
   at ZSchemeGenerated.NonexhModule.Main() in nonexh.cs:line 22
Aborted (core dumped)
```

The emitted C# shows the missing case being papered over with a throwing
fallback arm:

```csharp
public abstract record Shape;
public sealed record Circle(int Radius) : Shape;
public sealed record Rect(int W, int H) : Shape;
public sealed record Tri(int B, int H) : Shape;

public static int Area(Shape s)
{
    return s switch {
        Circle(var r) => (r * r),
        Rect(var w, var h) => (w * h),
        _ => throw new System.InvalidOperationException("Non-exhaustive match"),
    };
}
```

## Root cause

[`Types/ExhaustivenessChecker.cs`](../src/ZScheme.Compiler/Types/ExhaustivenessChecker.cs)
exists and is fully implemented — an 84-line simplified Maranget check with
`RegisterUnion(unionName, caseNames)`
([:16](../src/ZScheme.Compiler/Types/ExhaustivenessChecker.cs:16)) and
`Check(AstNode.Match, scrutineeTypeName)`
([:21](../src/ZScheme.Compiler/Types/ExhaustivenessChecker.cs:21)), which reports:

- `Error` — `"Non-exhaustive match: missing cases {…}"` for unions ([:46](../src/ZScheme.Compiler/Types/ExhaustivenessChecker.cs:46))
- `Warning` — `"Non-exhaustive match on Bool"` ([:62](../src/ZScheme.Compiler/Types/ExhaustivenessChecker.cs:62))
- `Warning` — `"Match on literals without a wildcard/default case may not be exhaustive"` ([:68](../src/ZScheme.Compiler/Types/ExhaustivenessChecker.cs:68))

**It is never instantiated anywhere in `src/`.** The only construction sites in
the entire repo are in its own unit test:

```console
$ grep -rn "ExhaustivenessChecker" --include=*.cs src/ tests/
src/ZScheme.Compiler/Types/ExhaustivenessChecker.cs:9:public sealed class ExhaustivenessChecker(...)
tests/ZScheme.Compiler.Tests/Types/ExhaustivenessTests.cs:25:  var checker = new ExhaustivenessChecker(diag, env);
tests/ZScheme.Compiler.Tests/Types/ExhaustivenessTests.cs:61:  var checker = new ExhaustivenessChecker(diag, env);
...  # 6 more, all in ExhaustivenessTests.cs
```

So the class is green in CI, fully covered by its own tests, and has **no
caller in the compiler**. `TypeInferer.InferUnionDecl`
([TypeInferer.cs:880](../src/ZScheme.Compiler/Types/TypeInferer.cs:880)) registers
union cases into the type env as ordinary values, but never calls
`RegisterUnion`; nothing calls `Check`.

Note the easy-to-confuse near-neighbour: `IrLowering.RegisterUnionCtor`
([IrLowering.cs:215](../src/ZScheme.Compiler/Ir/IrLowering.cs:215)), which *is*
live (called from `Compilation.cs:873` and
`Compilation.ModuleCompilation.cs:219`). It maps case name → union name for
constructor lowering, and is unrelated to exhaustiveness. Its existence makes
the checker look wired-up at a glance.

With no front-end check, "non-exhaustive" is left entirely to the backends,
which each independently inject the same runtime throw:

- `CSharpEmitter.Emit.cs:1333` — `_ => throw new System.InvalidOperationException("Non-exhaustive match")`
- `IlEmitter.Emit.cs:2120` — `ldstr "Non-exhaustive match"` + `throw`

(The two messages match exactly, which is deliberate — it's what makes the two
backends differentially comparable to the fuzzer. Any fix must keep them in
sync as the *last-resort* runtime guard.)

## Fix

Wire the checker into type inference. Sketch:

1. In `TypeInferer`, hold an `ExhaustivenessChecker` and call `RegisterUnion` from
   `InferUnionDecl` ([TypeInferer.cs:880](../src/ZScheme.Compiler/Types/TypeInferer.cs:880))
   with the union's case names.
2. Call `Check(match, scrutineeTypeName)` when inferring an `AstNode.Match`,
   passing the scrutinee's resolved `ZType.ZNamedType.Name` (there is no dedicated
   union `ZType` — unions, records, classes, and interfaces all reuse
   `ZNamedType`, so the name lookup against `_unionCases` is what distinguishes them).
3. Cross-module: unions imported from precompiled packages must also be
   registered, or matches on `stdlib` unions (`Option`, `Result`, `List`) will be
   silently unchecked. `MetadataSerializer` already round-trips `IrNode.UnionDecl`
   (`Cache/MetadataSerializer.cs:209-231,474-521`), and both backends already
   rebuild case tables from imports — so the case names are available; they just
   need to reach the checker.

### Things to watch when fixing

- **The checker's non-union paths are guarded by `scrutineeTypeName is null`**
  ([:53](../src/ZScheme.Compiler/Types/ExhaustivenessChecker.cs:53)). Once a real
  scrutinee type name is threaded through, the Bool and literal branches become
  unreachable for any typed scrutinee. The bool check in particular will need its
  condition reworked (a `Bool` scrutinee will presumably arrive as a named/primitive
  type, not `null`).
- **Expect existing code to fail the new check.** `packages/stdlib`,
  `packages/zunit`, `examples/`, and the test corpus have never been exhaustiveness-
  checked. Land the union case as an `Error` only after the tree is clean; consider
  staging it as a `Warning` first.
- **The fuzzer generates matches** (`MatchExprGenerator`) and may currently emit
  non-exhaustive ones that would newly become compile errors — check
  `src/ZScheme.Fuzzer/Generation/MatchExprGenerator.cs` before turning the error on,
  or the fuzzer will start reporting its own generated programs as failures.
- **Keep the backend runtime throws.** They remain the correct guard for cases the
  front-end check can't prove (literal matches, CLR-typed scrutinees).

## Related

See [`pattern-compiler-dead-and-stale.md`](./pattern-compiler-dead-and-stale.md) —
`Ir/PatternCompiler.cs` is the other never-instantiated component in this area,
and its `CompileArms` empty-arms path emits a call to an undefined
`__match_failure` variable with the comment *"should be caught by exhaustiveness
checker"* — a direct dependency on the check that this issue reports as missing.
