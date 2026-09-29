# The C# backend emits a case-variant class's constructor call with the source spelling

**Found by:** the fuzzer's new non-canonical type-name coverage (2026-09-29
fuzz session, cases `98654f88`, `7355a983`, `97bc0a2a`, `84e45d43`,
`dc41096e`, `1f955eac`; minimized by hand). Type names resolve by declaration
and pattern context, not capitalization (`d58beca1`), so the generator now
occasionally spells a class name with a lower-case first letter.

**Affects:** the C# backend, for classes declared with an **explicit**
constructor and a non-canonical name. The declaration is emitted under the
canonicalized name (`fCls_0` → `FCls_0`, via `NameConverter`) but constructor
calls are emitted verbatim (`new fCls_0(5)`), so Roslyn rejects the output:

```text
error CS0246: The type or namespace name 'fCls_0' could not be found
```

The IL backend emits the canonical name and runs; the front end resolves the
reference case-insensitively. Only the C# constructor-call path diverges.
Notably, the **implicit**-constructor path canonicalizes correctly
(`new FCls_0(F0: 5)` — named field arguments), so the gap is specific to the
explicit-constructor call emission.

## Symptom

`zs-fuzz --repro` on the repro prints `[diffexec] FAIL: Roslyn failed to
compile C# output` with CS0246 naming the lower-case spelling.

Repro:
[`repros/csharp-case-variant-explicit-ctor.zs`](repros/csharp-case-variant-explicit-ctor.zs).

## Root cause

The explicit-constructor call site in `CSharpEmitter` formats `new {name}(...)`
from the AST's source text instead of routing the type name through
`NameConverter.SanitizeIdentifier` (or the emitter's emitted-name table, which
already answers for the declaration).

## Suggested fix direction

Canonicalize the constructed type's name at the explicit-constructor call
site the way the implicit-constructor call site already does. A regression
test wants a case-variant class with an explicit constructor compiled through
both backends; the fuzzer now generates the shape (gated low while this
diverges).
