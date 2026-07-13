# `define-async`: a multi-expression body silently drops everything AFTER the first expression

**Found by:** code inspection while investigating the sibling `define`/`lambda` body
bug, confirmed with a compile-and-inspect repro. Pre-existing, not a regression.

**Affects:** `AstBuilder.BuildDefineAsync`
(`src/ZScheme.Compiler/Ast/AstBuilder.cs:2579`). Any `define-async` whose body has
**two or more** expressions keeps only the first and silently discards the rest.
Both backends are affected identically (the expressions are gone before IR lowering),
so the differential fuzzer cannot see it.

This is **silent code deletion** — no diagnostic, no warning.

Note this is the *opposite* failure mode from the sibling bug in `define`/`lambda`,
which drops the **first** expression and keeps the rest. Same area, different cause.

## Symptom

```scheme
(define-async (asyncfn) : Unit
  (write-line "S1")
  (write-line "S2")   ; ← silently discarded
  (write-line "S3"))  ; ← silently discarded
```

Emitted C#:

```csharp
public static async System.Threading.Tasks.Task Asyncfn()
{
    System.Console.WriteLine("S1");
}
```

## Root cause

Unlike `BuildDefine` (`:440-444`) and `BuildLambda` (`:727-731`), which at least
attempt to sequence a multi-expression body, `BuildDefineAsync` simply builds the
single s-expression at `bodyStart` and ignores every item after it:

```csharp
// AstBuilder.cs:2573-2580
if (bodyStart >= list.Items.Count)
{
    diagnostics.Error("Async function definition requires a body", list.Span);
    return new AstNode.UnitLit(list.Span);
}

var body = Build(list.Items[bodyStart]);   // <-- only the first body form; rest dropped
return new AstNode.DefineAsync(
```

There is no `remainingItems` handling at all — the multi-expression case was never
implemented.

## Suggested fix direction

Mirror whatever `BuildDefine` ends up doing once the sibling bug is fixed. Both
should route through one shared body-sequencing helper:

```csharp
var remainingItems = list.Items.Skip(bodyStart).ToList();
var body = remainingItems.Count == 1
    ? Build(list.Items[bodyStart])
    : BuildExprSequence(remainingItems, list.Span);
```

(`BuildExprSequence` is the helper proposed in the sibling issue — the point of
fixing them together is that all three body-building sites should share it rather
than each re-deriving the sequencing.)

Worth checking that the resulting `let`-spine sequencing composes correctly with
`await` inside an async body — `AsyncStateMachineAnalyzer.ContainsAwait`
(`Codegen/AsyncStateMachineAnalyzer.cs`) must still see awaits that now sit inside
nested `let` bindings rather than at the body root.

## Test coverage gap

Same gap as the sibling issue: no test exercises a multi-expression async body for
its side effects. Add one that asserts all statements run, and one with an `await`
in a non-final position.

## Priority note

High, same class as the sibling. Arguably worse in practice: async bodies are exactly
where imperative multi-statement sequences (await, log, await, return) are idiomatic,
so a `define-async` that does more than one thing is silently truncated to its first
statement.

Sibling issue in the same body-building code:
[define-lambda-multi-expr-body-drops-first-expression.md](define-lambda-multi-expr-body-drops-first-expression.md)
— fix together.
