# The IL backend marks overrides final, so any further override fails type load

**Found by:** the fuzzer's new multi-level inheritance and
`#:open`+interface coverage (2026-09-29 fuzz session, cases `1b024ffc`,
`a9c07a8b`, `c0345792`; minimized by hand). Two generator shapes reach it:

* a subclass re-overriding a method its base implemented for an interface
  (`#:open` base implementing an interface, derived class overriding that
  method), and
* a three-level chain where the bottom level overrides a method the middle
  level already overrode.

**Affects:** the IL backend only. The C# backend compiles the same programs
and they run; on IL the assembly fails to load, or the IL *compiler* itself
throws the same `TypeLoadException` while reflecting on emitted metadata.

## Symptom

```text
System.TypeLoadException: Unable to load one or more of the requested types.
Declaration referenced in a method implementation cannot be a final method.
Type: 'ZSchemeFuzzed.FCls_1'.  Assembly: 'ZSchemeFuzzed, ...'
```

At diff-exec this surfaces as `[IL] threw ... ReflectionTypeLoadException`
against `[CS] returned <value>`; when the backend reflects during emission it
surfaces as an IL compile exception.

Repros:
[`repros/il-final-override-chain.zs`](repros/il-final-override-chain.zs)
(subclass re-overrides an interface-implemented method) and
[`repros/il-final-override-three-level.zs`](repros/il-final-override-three-level.zs)
(override of an override).

## Root cause

When the IL emitter implements an interface method — or marks a method that
overrides a base method — it declares the method `final` (correct for a sealed
class, whose overrides cannot be overridden further). A derived class that
overrides the same slot then emits a method-implementation entry referencing
that final method, which the CLR rejects at type load: an implementation
declaration must be virtual.

`#:open` classes promise their methods stay overridable, and an interface
implementation inherited by a subclass must stay overridable too (a subclass
that re-implements the member is ordinary C#). The emitter needs the same
distinction the C# compiler makes for free: mark the slot `final` only when
the declaring class is not `#:open` *and* no subclass in the (known) program
overrides it further.

## Suggested fix direction

In the IL emitter's override/interface-implementation marking, treat the
method as final only when the declaring class is sealed (not `#:open`) and no
emitted subclass overrides the same method name. The subclass table exists
already — derived classes carry `BaseClassName` and the emitter resolves
inherited members through the chain (post-`840cb073`).
