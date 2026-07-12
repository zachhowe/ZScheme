# `Ir/PatternCompiler.cs` is dead code, and it has drifted badly enough that reviving it as-is would miscompile

**Found by:** manual audit while evaluating .NET 11 union types (2026-07-12).

**Affects:** nothing at runtime — the class never executes. The cost is entirely
in maintenance and misdirection: it is documented as a live pipeline sub-pass, it
is referenced by name in three fuzzer coverage comments, and it is green in CI via
its own unit tests. A reader has every reason to believe it is load-bearing, and
it is not.

**Severity:** medium (correctness risk is latent, not active). The danger is that
someone "fixes a bug" in `PatternCompiler` and it changes nothing, or wires it in
and it silently miscompiles.

## Symptom 1 — it is never instantiated in `src/`

```console
$ grep -rn "PatternCompiler" --include=*.cs src/ tests/
src/ZScheme.Compiler/Ir/PatternCompiler.cs:9:public sealed class PatternCompiler
src/ZScheme.Fuzzer/Generation/MatchExprGenerator.cs:16:  //  the PatternCompiler's nested decision-tree path   <-- comment only
src/ZScheme.Fuzzer/Generation/UserTypeGenerator.cs:76:  //  PatternCompiler's nested decision-tree path.       <-- comment only
src/ZScheme.Fuzzer/Generation/Stdlib/StdlibListGenerator.cs:12: // key win for PatternCompiler coverage        <-- comment only
tests/ZScheme.Compiler.Tests/Ir/PatternCompilerTests.cs:20:  var compiler = new PatternCompiler();
...  # 7 more, all in PatternCompilerTests.cs
```

The only `new PatternCompiler()` calls in the repo are in its own test file.
`IrLowering` never constructs it. **`IrNode.Match` survives lowering intact and
each backend compiles it independently:**

- C# — `CSharpEmitter.Emit.cs:1244` `EmitMatch` emits a native C# `switch`
  expression with positional/recursive patterns, deferring reachability to Roslyn.
- IL — `IlEmitter.Emit.cs:2148` `EmitPatternTest` → `:2327` `EmitConstructorPatternTest`
  emits an `isinst` + `brfalse` decision chain directly.

So the "compile match to a decision tree" pass genuinely happens — twice, in the
backends — just never in `PatternCompiler`.

## Symptom 2 — three fuzzer generators claim to exercise it

These comments are actively misleading. The generators are fine and the coverage
they describe is real; it just lands in the **backends'** nested-pattern paths, not
in `PatternCompiler`:

- `src/ZScheme.Fuzzer/Generation/MatchExprGenerator.cs:16` — *"the PatternCompiler's nested decision-tree path … get[s] exercised"*
- `src/ZScheme.Fuzzer/Generation/UserTypeGenerator.cs:76` — *"exercise the PatternCompiler's nested decision-tree path"*
- `src/ZScheme.Fuzzer/Generation/Stdlib/StdlibListGenerator.cs:12` — *"the key win for PatternCompiler coverage"*

## Symptom 3 — the docs list it as a live sub-pass

`docs/COMPILER-PIPELINE.md:38` and `:227` present it as one of the three IR
lowering sub-passes alongside `ClosureConverter` and `TailCallAnalyzer`, which
*are* live. (Docs intentionally left unchanged pending a decision on this issue.)

## Root cause — the drift

If someone did wire it in today, it would miscompile. The class was written
against an earlier union layout and has not tracked the backends.

### 3a. Field access uses `Item1`/`Item2`, but union cases have named fields

`PatternCompiler.cs:87` (tuple) and `:119` (constructor) read fields positionally:

```csharp
var fieldAccess = new IrNode.FieldGet(scrutinee, $"Item{i + 1}") { … };
```

`Item1`/`Item2` is `ValueTuple` shape. Union case fields are **named after the
ZScheme field**. For `(define-union Shape (Circle [radius : Int]) (Rect [w : Int] [h : Int]))`
the C# backend (`CSharpEmitter.Emit.cs:1977`) emits:

```csharp
public abstract record Shape;
public sealed record Circle(int Radius) : Shape;
public sealed record Rect(int W, int H) : Shape;
```

and the IL backend (`IlEmitter.Define.cs:1188-1227`) emits a `<Radius>k__BackingField`
plus a `get_Radius` property. There is no `Item1` on either. A `FieldGet(_, "Item1")`
against a union case resolves to nothing.

The tuple path (`:87`) may be correct for `ValueTuple`, but the constructor path
(`:119`) is not.

### 3b. Nested constructor patterns silently discard their sub-conditions

`PatternCompiler.cs:126-129`:

```csharp
var (subCond, subBindings) = CompilePattern(fieldAccess, ctor.Fields[i], span);
bindings.AddRange(subBindings);
// sub-conditions would need to be ANDed together (simplified here)
```

`subCond` is computed and **thrown away**. Only the outer `typeTest` is returned
(`:131`). So `(Cons h (Cons h2 _))` would compile to "is it a `Cons`?" and match
*any* `Cons`, including a one-element list — then read `h2` off a `Nil`.

This is precisely the nested-pattern shape the three fuzzer comments above claim
to be covering, which is a good illustration of the hazard: the fuzzer exercises
the backends, finds nothing wrong, and the reader concludes `PatternCompiler` is
validated.

The tuple path at `:94-101` *does* AND its sub-conditions together correctly — so
the two paths disagree with each other.

### 3c. The no-arms fallback calls an undefined variable

`PatternCompiler.cs:23-32`, reached when a match runs out of arms:

```csharp
if (arms.Count == 0)
    // No arms — should be caught by exhaustiveness checker
    return new IrNode.Call(
        new IrNode.Var("__match_failure") { Type = ZType.Unit, Span = matchSpan }, []);
```

`__match_failure` is **defined nowhere** — not in `src/`, not in `packages/`:

```console
$ grep -rn "__match_failure" --include=*.cs --include=*.zs src/ packages/ | grep -v PatternCompiler.cs
(no results)
```

Note the comment's assumption: *"should be caught by exhaustiveness checker."*
That checker is itself never wired in — see
[`exhaustiveness-checker-never-wired-into-pipeline.md`](./exhaustiveness-checker-never-wired-into-pipeline.md).
Both halves of the pattern-matching safety story are dead, and each one's comments
assume the other is alive.

### 3d. Smaller defects

- **`:107` — non-unique type-test binding.** The cast temp is named
  `$"__{ctor.Name}_val"`, keyed only on the case name. Two patterns over the same
  case in one match (or a `Cons` nested inside a `Cons`) collide on the same
  variable.
- **`:89`, `:122` — every extracted field is typed `ZType.Unit`.** `FieldGet` nodes
  are built with `Type = ZType.Unit` regardless of the field's real type, so
  downstream typing of any binding is wrong.
- **`:73` — unknown literals become `0`.** The literal switch falls back to
  `_ => new IrNode.IntConst(0)`, silently turning an unrecognised literal pattern
  into a match against integer zero rather than failing loudly. (This is the same
  class of bug as the `IrPattern.Literal { Value: float }` fall-through that caused
  a real miscompile in the IL backend — see the comment at `IlEmitter.Emit.cs:2191`
  citing fuzzer seed `0xf0ab7e8f`.)
- **`:42-47` — dead branch.** `if (remaining.Count == 0 && condition is null) return body;`
  is immediately followed by `if (condition is null) return body;`. The first `if`
  is subsumed by the second.

## Fix — pick one

**Option A — delete it (recommended).** Delete `Ir/PatternCompiler.cs` and
`tests/ZScheme.Compiler.Tests/Ir/PatternCompilerTests.cs`; drop the three stale
fuzzer comments; drop the two `docs/COMPILER-PIPELINE.md` references. Both
backends already own match compilation and are the only implementations the fuzzer
actually validates. This removes ~150 lines of code, ~200 lines of tests that
assert behaviour nothing depends on, and the false impression of a shared lowering.

**Option B — revive it as the single shared lowering.** Genuinely attractive in
principle: it would collapse two independent match compilers into one, and the
duplication between `CSharpEmitter.EmitMatch` and `IlEmitter.EmitPatternTest` is
exactly where differential bugs have come from. But it is a real project, not a
cleanup — every defect in §3 must be fixed first, and the C# backend would be
giving up native `switch`-expression emission (and with it Roslyn's reachability
analysis, `PruneUnreachableArms`, and the positional-deconstruction machinery at
`CSharpEmitter.Emit.cs:1339-1584`) in exchange for pre-lowered if/typetest chains.
That is a legible trade but it needs its own design pass.

Do **not** leave it in its current state. Dead code that three comments and one doc
page describe as live is worse than either outcome.
