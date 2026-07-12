# IL backend: `=` / `!=` on String compares references, not values

**Status:** open
**Found by:** fuzzer generator work (hand-written smoke test for new string-append chain shapes), 2026-07-11
**Oracle:** diffexec — `Compute() return diverged (IL=0, CS=1)`

## Minimal repro

```scheme
(namespace ZSchemeFuzzed)

(module repro)

(define (compute) : Int
  (if (= (string-append "a" "b") "ab") 1 0))
```

- C# backend: returns `1` (correct — string value equality)
- IL backend: returns `0`

Run with: `zs-fuzz --repro <file.zs>`

## Root cause

`IlEmitter.Emit.cs` lowers the polymorphic `=` binop to a bare `ceq`
(`EmitBinOpCore`, ~line 4738), and `!=` to `ceq` + negate (~line 4747). For
`String` operands `ceq` is **reference** equality. `string-append` lowers to
`String.Concat`, whose result is a fresh (non-interned) instance, so a
computed string never reference-equals a literal even when the contents match.

The C# backend emits `l == r`, which for statically-typed `string` operands is
`String.op_Equality` — **value** equality. The two backends therefore disagree
exactly when contents are equal but instances differ.

Note the IL **match-pattern** path does this correctly: string literal
patterns call `String.Equals(string, string)` (`EmitPatternTest`,
IlEmitter.Emit.cs ~line 2213). Only the expression-level `=` / `!=` binop is
affected.

## Why the fuzzer missed it until now

Both sides of the fuzzer's `(= s1 s2)` shapes were almost always either equal
*literals* (interned → `ceq` true → agree) or unequal contents (both false →
agree). The divergence needs equal contents with at least one computed
operand, which the new deep `string-append` chain shapes now produce
routinely.

## Suggested fix direction

In the IL emitter's binop lowering, special-case `=` / `!=` when the operand
type is String to call `String.Equals(string, string)` (mirroring the pattern
path at ~2213), leaving `ceq` for value types and interned symbols.

## Expected fuzzer impact

Until fixed, diffexec runs will report this shape frequently (the new
string-append chains fire in ~25% of `string-append` sites). If the artifact
stream gets too noisy before a fix lands, gate the *computed-vs-literal
equality* shape low (mirroring the is-null? precedent) — but prefer fixing the
emitter; the shape is otherwise valuable.
