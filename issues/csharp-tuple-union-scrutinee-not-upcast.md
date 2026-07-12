# C# backend: union value inside a `values` tuple scrutinee is not upcast, breaking cross-ctor arms

**Status:** open
**Found by:** fuzzer generator work (new tuple-of-union match shape), 2026-07-11
**Oracle:** diffexec — `Roslyn failed to compile C# output` (CS8121); IL backend compiles and runs correctly.

## Minimal repro

```scheme
(namespace ZSchemeFuzzed)

(module repro)

(define-union (Pair2 ^a)
  (MkNone)
  (MkPair [x : ^a] [y : ^a]))

(define (compute) : Int
  (match (values (MkPair 3 4) 10)
    [(values MkNone k) k]
    [_ -2]))
```

- IL backend: compiles, runs, returns -2 (correct).
- C# backend: Roslyn error
  `CS8121: An expression of type 'ReproModule.MkPair<int>' cannot be handled by a pattern of type 'ReproModule.MkNone<int>'.`

Run with: `zs-fuzz --repro <file.zs>`

## Root cause

The C# emitter emits the tuple scrutinee as
`(new MkPair<int>(3, 4), 10) switch { (MkNone<int>, var k) => ..., _ => ... }`.
The tuple element keeps its natural (concrete ctor) type `MkPair<int>`, so a
pattern testing any *other* ctor of the union is provably impossible and
Roslyn rejects it.

Direct union matches (`(match (MkPair 3 4) [MkNone ...] ...)`) do not hit
this: the emitter's direct-scrutinee path presents the union base type. Nested
ctor patterns inside another ctor also work, because record fields are
declared at the base type (`Pair2<T0> Tail`). Only *tuple elements* built
inline from a ctor keep the concrete type.

## Suggested fix direction

When emitting a `values` tuple scrutinee whose element's IR type is a union,
insert an explicit upcast to the union base type (e.g.
`((Pair2<int>)new MkPair<int>(3, 4), 10)`), mirroring whatever the direct
union-scrutinee path does.

## Fuzzer note

`MatchExprGenerator.GenTupleOfUnionMatch` deliberately emits the
cross-ctor-arm shape at a **low gate (0.05)** so the repro stays present in
the artifact stream without dominating it (is-null? precedent). Once this is
fixed, raise the mismatch probability back to ~0.2 for real miss-path
coverage.
