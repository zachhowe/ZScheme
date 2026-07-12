# C# backend: comparison-chain fresh names (`$cmp_N` / `$neq_N`) emitted verbatim — invalid C#

**Status:** open
**Found by:** fuzzer generator work (new n-ary comparison-chain shapes), 2026-07-11
**Oracle:** diffexec — `Roslyn failed to compile C# output` (CS1056 "Unexpected character '$'" plus cascading parse errors); IL backend compiles the same program fine.

## Minimal repro

Any comparison chain whose *middle* operand is impure (not a literal/variable),
so AstBuilder's chain expansion binds it to a fresh `$cmp_N` variable:

```scheme
(namespace ZSchemeFuzzed)

(module repro)

(define (compute) : Int
  (if (< 1 (+ 2 3) 9) 1 0))
```

The C# emitter produces IIFE-shaped binds like:

```csharp
((System.Func<int, bool>)((int $cmp_0) => (1 < $cmp_0) && ($cmp_0 < 9)))(2 + 3)
```

`$` is not valid in a C# identifier, so Roslyn rejects the file. The `!=`
all-distinct expansion has the same problem via `$neq_N`.

- IL backend: compiles and runs (IL allows `$` in names).
- C# backend: Roslyn parse errors → diffexec FAIL.

Run with: `zs-fuzz --repro <file.zs>`

## Root cause

`AstBuilder.ExpandComparisonChain` / `ExpandNeqAllDistinct` synthesize fresh
binder names beginning with `$` (e.g. `$cmp_0`). The C# emitter emits local
binder names verbatim for these lambda parameters instead of routing them
through the identifier sanitizer (`NameConverter`), which other generated
names go through.

## Suggested fix direction

Sanitize the fresh chain names in the C# emitter's binder-emission path (or
generate `__cmp_N`-style names in AstBuilder, which are valid in both
backends).

## Fuzzer note

The comparison-chain generators (`ExprGenerator.GenComparison` /
`GenFloatComparison`) keep chain *middle* operands to pure leaf shapes
(literals / in-scope vars) with only a **5% gate** on impure middles, so this
repro stays present without flooding the artifact stream (it hit ~40% of all
cases when middles were unrestricted). Once fixed, lift the middle operands
back to full `GenInt`/`GenFloat` expressions.
