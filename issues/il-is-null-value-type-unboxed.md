# IL backend emits unverifiable IL for `is-null?` over a value-type operand (missing box)

**Found by:** fuzzer run, seed `0x00000007`, 1200 iterations, after wiring a new
low-gated `is-null?` generator (`stdlib/core`) into `ExprGenerator.GenInt`.

**Affects:** all 8 `ilverify` failures in this run — every one of them contains
an `is-null?` call. Across the run, `is-null?` appears in 73 programs of which
**48 fail `ilverify` (~66%)** and 25 pass; no failure in the run lacks
`is-null?`. (An earlier seed-`0x1` run, before the generator's gate was lowered,
showed the same pattern at larger scale: 48/48 `ilverify` failures contained
`is-null?`.)

Only the **IL** backend is affected — `CompileConsistencyOracle` and
`DifferentialExecOracle` both pass on these cases (the unverifiable IL still
*executes* and returns the same `Int` as the C# backend), so this is caught
solely by `IlVerifyOracle`.

**Representative failing seeds (seed `0x00000007` run):** `3f4818cc`,
`45ab6826`, `577ccf56`, `5f98aebf`, `c3003eca`, `cdfb5966`, `d2c0e57a`,
`f9dfc43c`.

Repro (deterministic — the seed + iteration count fully determine the case set):

```
dotnet run -c Release --project src/ZScheme.Fuzzer -- --seed 7 -n 1200 --oracles compile,ilverify
```

## Symptom

`dotnet ilverify` rejects the IL `.dll` with a stack-type error at the
`is-null?` call site — the value-type operand is left on the stack as `Int32`
where `System.Object.ReferenceEquals(object, object)` expects a `ref 'object'`:

```
[IL]: Error [StackUnexpected]: [...ZSchemeFuzzed.Fuzz_<hex>Module::__lambda_25___lambda_67_333(int32)]
      [offset 0x00000005][found Int32][expected ref 'object'] Unexpected type on the stack.
[IL]: Error [StackUnexpected]: [...ZSchemeFuzzed.Fuzz_<hex>Module::F2(int32, int32)]
      [offset 0x000006CB][found Int32][expected ref 'object'] Unexpected type on the stack.
```

The failing methods are consistently ones where the `is-null?` call sits inside
a **nested lambda / higher-order-function argument** (e.g.
`(lambda ([x : Int]) (if (is-null? x) 1 0))`) or a generated user function
(`F2(int32, int32)`), rather than at the top level of `compute`.

## Root cause (suspected)

`is-null?` (`packages/stdlib/src/core.zs`, `(^a -> Bool)`) lowers to
`System.Object/ReferenceEquals(x, null)`. `ReferenceEquals` takes two `object`
parameters, so a value-type argument (`Int`, a boxed literal, a `string-append`
result, etc.) must be **boxed** before the call. The IL backend appears to omit
the `box` instruction in some contexts — leaving a raw `Int32` on the stack
where a `ref 'object'` is required — whereas the C# backend boxes correctly
(hence C#-side compile + execution succeed and `DifferentialExecOracle` agrees).

This matches the pre-existing note in
[`StdlibCoreGenerator.cs`](src/ZScheme.Fuzzer/Generation/Stdlib/StdlibCoreGenerator.cs)
that `is-null?`'s boxing path "differs between the C# and IL backends" — this
report confirms that suspicion with a concrete verifier error.

**Note on reduction:** trivial top-level repros —
`(if (is-null? 5) 1 0)`, `(is-null? x)` over an `Int` parameter/`let`-binding,
and `is-null?` inside a bare lambda IIFE — all **verify clean**, so the missing
box is *context-dependent* (it manifests in specific nested-closure / value-flow
positions, not for every `is-null?` over a value type). The failing cases from
the run above are the reliable reproducers; a minimal standalone `.zs` will
require bisecting one of them (start from the smaller artifacts and reduce
toward the `__lambda_*` / `F2` method the verifier names).

## Suggested fix direction

In the IL emitter's lowering of `is-null?` / `ReferenceEquals` (see
`Codegen/IlEmitter*.cs`; cross-check against how `CSharpEmitter` handles the same
call), ensure a `box` is emitted for a value-type operand in **all** positions,
including inside lifted closures where the operand's static type is a generic
type variable instantiated at a value type. The C# backend's handling of the
same call is the reference for correct behavior.

## Priority note

Lower severity than a miscompilation: the emitted IL still runs and produces the
correct result under the runtime's relaxed (non-verifying) load, so
`DifferentialExecOracle` does not flag it — the observable impact is
unverifiable IL, which matters for `ilverify`-gated CI and any consumer that
loads with full verification. The `is-null?` generator is gated low
(`ProgramGenerator`, ~8% of `stdlib/core`-importing programs) specifically so
this known-failing shape stays a present-but-non-dominating repro in the
artifact stream until this is fixed.
