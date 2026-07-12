# C# backend: shadowed local binders emit CS0136/CS0128 in nested contexts

**Status:** open
**Found by:** fuzzer generator work (new binder-shadowing probes), 2026-07-11
**Oracle:** diffexec — `Roslyn failed to compile C# output`; the IL backend compiles the same programs fine (single-backend failure = genuine emit bug).

## Symptom

With the fuzzer's `EnableShadowing` probe on (binder sites occasionally rebind
an in-scope name of the same type — a legal ZScheme shadow), the C# backend
frequently emits code Roslyn rejects with:

- `CS0136: A local or parameter named 'xN' cannot be declared in this scope
  because that name is used in an enclosing local scope ...` (dominant), or
- `CS0128: A local variable or function named 'xN' is already defined in this
  scope` (smaller cluster, often with CS0841 use-before-declaration cascades).

In a 3000-case run at seed `99991`, ~161 cases failed this way — the single
largest failure class of the run.

## What is known about the trigger

Trivial shadows compile fine on both backends (all verified via `--repro`):

```scheme
(let ([x0 5]) ((lambda ([x0 : Int]) (+ x0 1)) x0))   ; ok
(let* ([x0 1] [x0 (+ x0 2)]) x0)                     ; ok
(let ([x0 7]) (match (values x0 2) [(values x0 k) (+ x0 k)]))  ; ok
```

The failures need deeper nesting — a shadow binder inside a larger expression
where the emitter's C# scoping (statement-lowered lets / lambda bodies /
switch-expression arms sharing one C# block) places the redeclaration into a
scope that already sees the outer name. The emitter has rename machinery for
match binders (`_patternRenames`) but the let/lambda paths evidently bypass
it.

A full (non-minimal) failing program is preserved at
`issues/repros/fuzz-failure-02380b13-cs0136.zs`
(`error CS0136 ... named 'x119'`). Re-running the fuzzer reproduces the class
readily: `zs-fuzz --seed 99991 -n 3000`.

## Suggested fix direction

Route all local binder names through a per-scope uniquifier in the C# emitter
(extend the `_patternRenames` approach to let/let*/lambda-lowered locals), or
emit genuinely nested C# blocks so ZScheme's innermost-binding-wins scoping
maps onto C# scoping directly.

## Fuzzer note

`ProgramGenerator` gates `EnableShadowing` at **0.10** (down from the intended
0.25) so this bug doesn't dominate the artifact stream. Raise it back once
fixed.
