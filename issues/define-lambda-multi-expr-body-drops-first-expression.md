# `define` / `lambda`: a multi-expression body silently drops its FIRST expression

**Found by:** manual end-to-end verification while making `string-append` variadic
(the scratch program's first `println` never printed). Reproduced on a clean tree
with unrelated changes stashed, so this is pre-existing, not a regression.

**Affects:** `AstBuilder.BuildDefine` (`src/ZScheme.Compiler/Ast/AstBuilder.cs:444`)
and `AstBuilder.BuildLambda` (`src/ZScheme.Compiler/Ast/AstBuilder.cs:731`). Any
`define` or `lambda` whose body has **two or more** expressions loses the first one.
Both backends are affected identically (the expression is gone before IR lowering),
so the differential fuzzer cannot see it.

This is **silent code deletion** — no diagnostic, no warning, correct-looking output.

## Symptom

```scheme
(define (three) : Unit
  (write-line "A1")   ; ← silently discarded
  (write-line "A2")
  (write-line "A3"))
```

Emitted C#:

```csharp
public static void Three()
{
    System.Console.WriteLine("A2");
    System.Console.WriteLine("A3");
}
```

Same for `lambda`:

```scheme
((lambda () (write-line "L1") (write-line "L2") (write-line "L3")))
;; prints L2, L3 — L1 is gone
```

A 2-expression body degenerates to just the second expression. A side-effecting
first statement (a logging call, a `set!`, a guard) vanishes without a trace.

An explicit `(begin ...)` wrapper is a correct workaround and emits all three:

```scheme
(define (three) : Unit
  (begin (write-line "A1") (write-line "A2") (write-line "A3")))  ; all three emitted
```

## Root cause

`BuildBegin` (`AstBuilder.cs:2383`) parses a *real* `(begin e1 e2 ... en)` s-expression,
so it treats `Items[0]` as the `begin` keyword and starts reading operands at index 1:

```csharp
private AstNode BuildBegin(SExpr.SList list)
{
    if (list.Items.Count < 2)
        return new AstNode.UnitLit(list.Span);

    if (list.Items.Count == 2)
        return Build(list.Items[1]);        // <-- index 1, not 0

    for (var i = 1; i < list.Items.Count - 1; i++)   // <-- starts at 1
    ...
```

But the implicit-body path synthesizes an `SList` containing **only the body
expressions** — with no `begin` atom at index 0:

```csharp
// AstBuilder.cs:440-444  (identical code at :727-731 for lambda)
var remainingItems = list.Items.Skip(bodyStart).ToList();
if (remainingItems.Count == 1)
    body = Build(list.Items[bodyStart]);
else
    body = BuildBegin(new SExpr.SList(remainingItems, list.Span));  // <-- no `begin` keyword
```

So `BuildBegin` consumes the first body expression as if it were the `begin` keyword
and discards it. The `remainingItems.Count == 1` branch is why single-expression
bodies — overwhelmingly the common case in this codebase's functional style — work
fine and hid the bug.

## Suggested fix direction

Split the operand-sequencing out of the keyword-parsing. Add a helper that takes the
expressions directly and have both callers use it:

```csharp
// Sequences body expressions: e1 e2 ... en → (let [_ e1] (let [_ e2] ... en))
private AstNode BuildExprSequence(IReadOnlyList<SExpr> exprs, SourceSpan span)

private AstNode BuildBegin(SExpr.SList list) =>
    BuildExprSequence(list.Items.Skip(1).ToList(), list.Span);   // drop the `begin` keyword here
```

then call `BuildExprSequence(remainingItems, list.Span)` at `:444` and `:731`.

Prepending a synthetic `begin` atom would also work but is the more fragile fix —
it re-enters the attribute-form handling in `BuildBegin`'s loop.

Note `BuildBegin` also carries attribute-form (`@`) handling in its loop; the
extracted helper must keep that, since attributes are legal in a function body.

## Test coverage gap

Nothing in the suite exercises a multi-expression `define`/`lambda` body for its
*side effects* — the existing tests use single-expression bodies, and the examples
and package tests only assert that things compile or that a returned value is
correct. A regression test should assert on observable effect ordering, e.g. a
function whose body appends to a list three times.

## Priority note

High. Silent, unconditional deletion of user code with no diagnostic, in one of the
most fundamental forms in the language. The blast radius is limited today only
because ZScheme code is written in a single-expression functional style — but any
imperative body (logging, `set!`, mutation, I/O sequences) is quietly wrong.

Sibling issue in the same body-building code, different failure mode:
[define-async-multi-expr-body-drops-all-but-first.md](define-async-multi-expr-body-drops-all-but-first.md)
— fix together.
