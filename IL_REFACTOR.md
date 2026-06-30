# Reducing IlEmitter Complexity

## The shape of the problem

`IlEmitter` is ~9,700 lines across 4 partial files, with `IlEmitter.Emit.cs`
alone at 6,239. Raw size isn't the real issue — IL emission is inherently
verbose. The complexity that *hurts* comes from four specific sources, and the
throughline across all of them is **divergent re-derivation**: type mapping and
overload resolution are each implemented twice (C#/IL backends) and at the wrong
stage (codegen instead of IR).

---

## 1. Two parallel type mappers (highest value, lowest risk)

`IlTypeMapper` (510 lines, returns reflection `Type`) and
`AsmResolverTypeMapper` (588 lines, returns AsmResolver `TypeSignature`) are
structurally identical twins:

| Concern              | IlTypeMapper                                  | AsmResolverTypeMapper                         |
| -------------------- | --------------------------------------------- | --------------------------------------------- |
| named type resolution| `ResolveClrNamedType`                         | `ResolveClrNamedType`                         |
| type lookup          | `FindClrType`                                 | `FindClrType`                                 |
| name conversion      | `ConvertToReflectionTypeName` / `ConvertTypeArg` | `ConvertToReflectionTypeName` / `ConvertTypeArg` |
| alias handling       | `ResolveAliasTarget` / `ApplyAlias` ×2        | `ResolveAliasTarget` / `ApplyAlias`           |
| func/tuple           | `MakeFuncType` ×2, `MakeValueTupleType`       | `MakeFuncType`, `MakeValueTupleInstance`      |

The *decision logic* (how a `ZType` maps to a CLR type — alias resolution, name
munging, func/tuple arity) is duplicated; only the final construction differs
(`Type.MakeGenericType` vs building a `GenericInstanceTypeSignature`). The string
helpers (`ConvertToReflectionTypeName`, `ConvertTypeArg`) are almost certainly
byte-identical and can be shared *today* with zero behavioral risk.

The deeper fix: factor the mapping into one traversal that emits an abstract
result, with a small `ITypeFactory<T>` (`MakeGeneric`, `MakeArray`, `MakeFunc`,
primitive lookups) implemented twice. That collapses ~1,100 lines to maybe ~650
and makes the two backends provably agree on type mapping — which matters
because the differential fuzzer exists precisely to catch where they *don't*.

---

## 2. Ambient `_current*` context (biggest correctness risk)

> **Status: DONE (A2 complete).** All 12 ambient fields below were bundled into an
> immutable `EmitContext` record, which is now threaded as an explicit parameter
> through `EmitNode`, every emission helper, the type mappers, and the root
> orchestration. There is no ambient mutable context field left at all — `_ctx`,
> `PushCtx`, and `CtxScope` were removed; context is derived purely with
> `ctx with { … }` and passed down. A mis-paired/stale context is now structurally
> impossible. `_currentFuncReturnType` was found dead (never read) and deleted; the
> monotonic counters `_asyncSmCounter`/`_lambdaId` correctly remain emitter-wide
> fields, not context. Verified: 0-warning build, full unit suite, all examples on
> both backends, package tests, and the differential fuzzer (no new C#-vs-IL
> divergence classes).

There are ~12 mutable fields forming an implicit "what am I emitting right now"
context, used 200+ times:

| Field                        | Usages |
| ---------------------------- | ------ |
| `_moveNextCtx`               | 24     |
| `_currentClassFields`        | 23     |
| `_instanceArgOffset`         | 22     |
| `_currentTypeVarMap`         | 20     |
| `_currentTypeParamMap`       | 19     |
| `_currentTypeDefinition`     | 18     |
| `_currentFuncReturnType`     | 16     |
| `_currentClassMethods`       | 14     |
| `_currentBaseTypeDefinition` | 9      |
| `_currentClassThisLocal`     | 5      |

`EmitNode` already cleanly threads `(il, outerParams, locals)` as parameters —
but the class/type/async context is ambient and must be saved/restored manually
around nested emission. This is the classic source of "works alone, breaks when
nested" bugs.

Fix: bundle these into a single immutable `EmitContext` record passed alongside
`il`, replacing manual save/restore with structured `context with { ... }`
derivation. High mechanical effort but it turns a whole bug *class* into
impossible states. Best done incrementally.

---

## 3. The call-emission family (most duplicated logic)

> **Status: DONE.** IR lowering is now the single overload-resolution authority for CLR
> calls. `ClrInterop.ResolveOverloadCallSite` was generalized to resolve from the full
> `(args -> ret)` call signature (CLR-assignability + nullable-unwrap + optional-param
> matching via the new `ArgBindsToParam`/`SelectOverload`), so every non-generic static
> `ClrCall` now carries a `ResolvedMethodInfo`. `EmitClrCall`'s ~120-line reflection
> overload fallback was deleted and it consumes the resolved method (using its
> `DeclaringType`, which also retired the `FindTypeForMember` `:from` re-disambiguation on
> that path); the static property/field fallback stays for non-method members. The built-in
> numeric conversions (`Int32.Parse`, `Convert.To*`) resolve up front via `BuiltinClrCall`.
> Instance `MethodCall` gained a `ResolvedMethodInfo` (new `ResolveInstanceOverloadCallSite`)
> populated for non-generic CLR receivers; `EmitMethodCall` prefers it and keeps its
> reflection chain only as a guarded fallback for generic receivers / properties / indexers.
> `SuperMethodCall` is intentionally unchanged — it binds against the in-flight base
> `TypeDefinition` (no reflection target; single-source on both backends). The C# backend is
> unchanged and serves as the Roslyn-resolved oracle. Verified: 0-warning build, full unit
> suite (+10 resolver tests), all examples on both backends, package tests, and the
> differential fuzzer (no new C#-vs-IL divergence classes; one pre-existing C#-backend CS1955
> case moved from the compile-noise bucket into diffexec because IL now compiles it).

`EmitClrCall`, `EmitCall`, `EmitMethodCall`, `EmitOutParamStaticCall`,
`EmitOutParamMethodCall`, `EmitSuperMethodCall` each re-run: overload resolution
→ generic closing → arg emission → nullable-wrap → emit call. `EmitClrCall`
alone is ~470 lines and re-does overload disambiguation that `ClrInterop`
*already did* during IR lowering. The comment at `IlEmitter.Emit.cs:1372` admits
it's re-disambiguating because the `:from` hint "is not carried on the emitted
ClrCall."

That's the tell: **the IR isn't carrying enough resolution, so codegen
re-derives it** — twice, once per backend, divergently.

Leverage move: push overload/generic resolution fully into IR lowering so the
`ClrCall`/`MethodCall` node carries a resolved target, and both emitters just
*emit* it. Shrinks both backends and removes a divergence source. This is the
refactor with real design choices to settle (what exactly the resolved target
looks like on the node), so it warrants a fuller plan before starting.

---

## 4. The async subsystem is a hidden separate class

`EmitAsyncFuncDef` → `EmitAsyncStubBody` → `EmitMoveNextMethod` →
`EmitMoveNextAwait` → `EmitSetStateMachineMethod` (~1,500 lines) plus
`AsyncStateMachineAnalyzer` (542) plus `_moveNextCtx` / `AsyncMoveNextContext` is
a cohesive state-machine generator entangled in the main emitter only through
shared helpers.

Fix: extract into `IlAsyncEmitter` (taking the host emitter for callbacks).
Removes ~1,500 lines and the `_moveNextCtx` / `_asyncSmCounter` ambient fields
from the main class. Clean, isolated, mostly mechanical.

---

## Recommended priority order

1. **Share the type-mapper string helpers now** — trivial, safe, immediate.
2. **Unify the two type mappers behind a factory** — high value, the fuzzer
   de-risks it.
3. **Carry resolved overloads on the IR node**, delete the emit-time
   re-resolution in both backends — biggest structural win, addresses root
   cause. Needs a design pass first.
4. **`EmitContext` record** to kill ambient `_current*` state — incremental.
5. **Extract `IlAsyncEmitter`** — clean, isolated, mostly mechanical.

The throughline: most of `IlEmitter`'s accidental complexity is **divergent
re-derivation** — type mapping and overload resolution each implemented twice
(C#/IL backends) and at the wrong stage (codegen instead of IR). Moving that work
*up* into the IR is what shrinks both backends at once.
