# The IL backend cannot resolve a super-call two hops up the hierarchy

**Found by:** the fuzzer's new multi-level inheritance coverage (2026-09-29
fuzz session, case `f4bb8e22`; minimized by hand). The generator started
emitting three-level class chains — grandparent `#:open`, middle `#:open`,
bottom — whose bottom level may override a method its direct base never did.

**Affects:** the IL backend only. The C# backend compiles the same program
(`base.M()` resolves through the whole chain), so this is a
compile-consistency divergence: C# succeeds, IL reports
`Error: Base class has no method 'M0_1'`.

## Symptom

```text
Error: Base class has no method 'M0_1' at <file>(16:35)
```

on the IL backend for a three-level chain where the bottom class calls
`(super/M0_1 ...)` and the *middle* class never declared `M0_1` — only the
grandparent did.

Repro: [`repros/il-two-hop-super-call.zs`](repros/il-two-hop-super-call.zs)
(`zs-fuzz --repro issues/repros/il-two-hop-super-call.zs`).

## Root cause

The IL emitter's `SuperMethodCall` lowering searches the *direct* base class's
declared methods and gives up — the same one-edge-only walk that
`88a3ab54` fixed for interface members and `Unifier.IsZSchemeSubtype`. A
super-call is legal whenever the method exists anywhere above the direct base:
the CLR's `base.M()` from `C : B : A` resolves to `A.M` when `B` does not
declare `M`, and the fuzzer's override picker deliberately produces that shape
(overriding a method inherited but not overridden by the direct base, to force
a two-level vtable insertion).

## Suggested fix direction

Walk the base-class chain (not just one hop) when resolving a
`SuperMethodCall`'s target, mirroring the user-type method-resolution walk the
IL backend already does for interface and base-class members
(`IlEmitter.Resolve`). Emit the call against the declaring type found.
