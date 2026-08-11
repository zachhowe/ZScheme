# A lambda that calls a class-hosted `letrec` / nested-`define` member passes the wrong `this` (IL backend)

**Found by:** fuzzer run, seed `0x5c1a55e5`, 1000 iterations
(`fuzz-runs/20260811-205845-seed5c1a55e5/`)

**Affects:** 40 of the 220 failures in this run, all `ilverify`. 13 of those carry
*only* this defect; the other 27 also carried the field/top-level-function shadowing
bug, an independent trigger with the same symptom, which has since been fixed (its
issue file is gone; see the "capture analysis mirrors EmitLoadVar" commit). Re-running
300 iterations of this seed after that fix leaves 7 failures, all of them this bug.

**Representative seeds:** `1c900912`, `3d13a8e5`, `5645c2b3`, `c4a7c7f3` (these four
have *only* this defect), plus `08f7df9f`, `0befa5eb`, `1004d995`.

**New on this branch.** The construct itself does not exist on `master` — the
minimized repro is a compile error there (`Type mismatch: '(Int -> Int)' vs 'Int'`),
and a `letrec` spelling of it is a parse error. This is a bug in the
`tco-in-classes-and-objects` feature, not a latent one it exposed.

Repro — as with the sibling issue, `--repro` on the *saved artifact* passes, because
the repro runner skips the `ilverify` oracle and the miscompiled lambda is usually
unreachable from `compute` in the generated program. The minimized repro does
diverge at runtime, so this works:

```
dotnet run --project src/ZScheme.Fuzzer -- \
  --repro issues/repros/lambda-loses-this-when-calling-class-hosted-group.zs
```

```
[compile] PASS: ok
[il-run] threw System.NullReferenceException: Object reference not set to an instance of an object.
[diffexec] FAIL: Compute() outcome diverged (one threw, one returned)
[IL] threw System.NullReferenceException
[CS] returned 2
```

and, for the verifier error itself:

```
zs compile issues/repros/lambda-loses-this-when-calling-class-hosted-group.zs \
   -b il -o /tmp/l/l.dll --package-path packages/stdlib
dotnet ilverify /tmp/l/l.dll -r <shared-framework>/*.dll -r /tmp/l/ZScheme.Runtime.dll \
   -r /tmp/l/zscheme-stdlib.dll
```

## Minimal repro

```scheme
(namespace ZSchemeFuzzed)
(module l)
(import stdlib/treelist)

(define-class FCls_0
  [f0 : Int #:mutable]                   ; #:mutable is load-bearing — see below
  (define (M0_1) : Int
    (define (x78 [n : Int]) : Int (if (<= n 0) f0 (x78 (- n 1))))
    (treelist-length
      (treelist-filter (treelist 1 2) (lambda ([x : Int]) (> (x78 x) 0))))))

(define (compute) : Int (FCls_0/M0_1 (FCls_0 5)))
```

`#:mutable` is what forces the group onto the class. With an immutable `f0` the
lifter captures the field by value and `x78` becomes an ordinary module-level static
(`KModule::__letrec_k_0_x78(int32, int32)`), which the lambda can call with no
receiver at all — that variant verifies clean. A `#:mutable` field needs a real
`this`, so the group is hosted on `FCls_0` as a private instance method, and that is
the shape that breaks. A sibling call or a `super/` call in the group body reaches
the same hosting decision and should be equally affected.

## Symptom

```
[IL]: Error [StackUnexpected]: [... : ZSchemeFuzzed.FCls_0::__lambda_5___lambda_10_39(int32)]
[offset 0x00000005][found Int32][expected ref '[ZSchemeFuzzed]ZSchemeFuzzed.FCls_0']
Unexpected type on the stack.
```

The lambda is emitted as a **static** method on the class and calls the hosted group
member with `ldarg.0` — its own `int32` parameter — as the receiver
(`fuzz-failure-3d13a8e5`):

```
### ZSchemeFuzzed.FCls_0::__lambda_13___lambda_55_21 System.Int32 *(System.Int32) static=True
  IL_0000: ldarg.0
  IL_0001: ldarg     System.Int32 x88
  IL_0005: callvirt  System.Int32 ZSchemeFuzzed.FCls_0::__letrec_fuzz_3d13a8e5_4_x78(System.Int32)
```

`fuzz-failure-5645c2b3` shows the more revealing form. There the lambda *did* get a
display class with a working `<>this` (loaded into `V_0` and used correctly for the
field reads at `IL_000E` and `IL_0030`) — and the group call in between still uses
`ldarg.0`:

```
### FCls_0+<>c____lambda_6___lambda_45_1307::Invoke instance System.Int32 *(System.Int32)
  IL_0000: ldarg.0
  IL_0001: ldfld    FCls_0 …+<>c____lambda_6___lambda_45_1307::<>this
  IL_0006: stloc    V_0
  IL_000A: ldloc    V_0
  IL_000E: ldfld    System.Int32 FCls_0::<F1>k__BackingField     <- correct
  …
  IL_0021: ldarg.0                                               <- wrong: the display class
  IL_0022: ldc.i4   9
  IL_0027: callvirt System.Int32 FCls_0::__letrec_fuzz_5645c2b3_3_x34(System.Int32)
```

## Root cause

Two separate places in the IL emitter, both needed for a complete fix.

**1. The call site never asks where `this` is.** `EmitCall`'s sibling-instance-method
branch (`src/ZScheme.Compiler/Codegen/IlEmitter.Emit.cs:2307-2331`) hardcodes the
receiver:

```csharp
// Load 'this' — from __this field if inside async state machine, else Ldarg_0
if (ctx.MoveNextCtx?.ThisField is { } siblingThisField)
{
    il.Add(CilOpCodes.Ldarg_0);
    il.Add(CilOpCodes.Ldfld, siblingThisField);
}
else
{
    il.Add(CilOpCodes.Ldarg_0);
}
```

That is an open-coded copy of `EmitLoadClassThis` (`IlEmitter.cs:1353`) missing its
first case — the one that redirects to `ctx.CurrentClassThisLocal` when we are inside
a lambda that captured the enclosing instance. Every other class-`this` consumer in
the emitter goes through `EmitLoadClassThis` (`:1339`, `:2438`, `:4030`, `:5217`);
this branch does not. `5645c2b3` is the direct proof: the local existed and was in
`ctx`, and this call site ignored it.

**2. The capture analysis doesn't know the call needs an instance.** Even with (1)
fixed, `CurrentClassThisLocal` is often null because no `<>this` was ever captured.
`EmitLambda`'s `needsThisCapture` scan (`IlEmitter.Emit.cs:3733-3755`) only tests free
variables against `ctx.CurrentClassFields` and only scans the body for class-field
*writes*. A call to a class-hosted `__letrec_*` member is neither: it resolves
through `ctx.CurrentClassMethods` at `:2310`, which the capture analysis never
consults. So a lambda whose only instance dependency is such a call captures nothing,
gets emitted as a bare static, and there is no `this` to load.

The hosting decision that creates these methods is `LetrecLifter`'s — a group inside a
class or `object` method that needs a real instance (a `#:mutable` field, a sibling
call, a `super/` call) becomes a private method on the declaring class rather than a
lifted static. `EmitLambda`'s capture analysis was written before that path existed
and was never extended to cover it.

## Suggested fix direction

- Replace the hand-rolled receiver load at `IlEmitter.Emit.cs:2313-2321` with a call
  to `EmitLoadClassThis(il, ctx)`. The `MoveNextCtx` case it handles is already the
  second case of that helper, so the two are the same logic minus the local.
- Extend `needsThisCapture` (`:3733`) to treat a call to a member of
  `ctx.CurrentClassMethods` as requiring `<>this`, the same way a class-field
  reference does. A body walk analogous to `BodyContainsClassFieldSet` looking for
  `IrNode.Call` against a hosted-method name is the smallest version of this.
- The generator already produces this shape unaided (`GeneratorContext.InInstanceContext`,
  §4.3 of `docs/FUZZER.md`), so a fix will be regression-covered by the next fuzz run
  — but it deserves a direct test in `Integration/LetrecTests.cs` /
  `NestedDefineTests.cs` too, since the class-hosted path is what this branch adds.

## Priority note

Now the highest-priority open issue: with the field-shadowing bug fixed, this is the
only remaining source of `ilverify` failures in the run, and the severity is
unchanged — silently invalid IL that
reinterprets an `int` as an object reference, for a program the C# backend compiles
correctly. Unlike the shadowing bug this one is *not* pre-existing; it ships with the
feature this branch is adding, so it should not land as-is.

Worth checking whether the async variant of the same call is affected in the other
direction: the `MoveNextCtx` branch at `:2315` is exercised by async class methods,
and if a lambda inside an async state machine calls a hosted group member, the same
`ctx` question arises there with a different answer.
