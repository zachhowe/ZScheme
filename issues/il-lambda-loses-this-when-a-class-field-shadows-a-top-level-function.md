# A lambda in a class method loses the enclosing instance when a class field shares a name with a top-level function (IL backend)

**Found by:** fuzzer run, seed `0x5c1a55e5`, 1000 iterations
(`fuzz-runs/20260811-205845-seed5c1a55e5/`)

**Affects:** 206 of the 220 failures in this run — every one of them an `ilverify`
failure. 13 of those 206 additionally carry the separate
`__letrec` receiver bug (see
`il-lambda-loses-this-when-calling-a-class-hosted-group-member.md`); the two
triggers are independent and frequently co-occur in the same generated program.

**Representative seeds:** `d655245d` (smallest, 2 KB), `0373fa51`, `03aedeb0`,
`0829ecc3`, `08f7df9f`, `ffdf0662` (the `ldloc` variant below).

Repro — `--repro` does **not** exercise this. The fuzzer's repro runner only runs
the `compile` and `diffexec` oracles (`ReproRunner.cs:55,88`), and in most of these
generated programs the miscompiled lambda is never reached from `compute`, so
`--repro` reports PASS on the saved artifact. Use the minimized file and the
verifier directly:

```
zs compile issues/repros/lambda-loses-this-when-field-shadows-toplevel-fn.zs \
   -b il -o /tmp/g/g.dll --package-path packages/stdlib
dotnet ilverify /tmp/g/g.dll -r <shared-framework>/*.dll -r /tmp/g/ZScheme.Runtime.dll \
   -r /tmp/g/zscheme-stdlib.dll
```

The minimized repro *is* also a runtime divergence, so this form reproduces it too:

```
dotnet run --project src/ZScheme.Fuzzer -- \
  --repro issues/repros/lambda-loses-this-when-field-shadows-toplevel-fn.zs
```

```
[compile] PASS: ok
[il-run] threw System.NullReferenceException: Object reference not set to an instance of an object.
[diffexec] FAIL: Compute() outcome diverged (one threw, one returned)
[IL] threw System.NullReferenceException
[CS] returned 2
```

## Minimal repro

```scheme
(namespace ZSchemeFuzzed)
(module g)
(import stdlib/treelist)

(define-class FCls_0
  [f0 : Int]
  (define (M0_0) : Int
    (treelist-length
      (treelist-filter (treelist 1 2) (lambda ([x : Int]) (> (+ x f0) 0))))))

(define (f0 [a : Int]) : Int a)          ; <- shares its name with the field above

(define (compute) : Int (FCls_0/M0_0 (FCls_0 5)))
```

Delete the `(define (f0 ...))` and the same program verifies and runs correctly.

## Symptom

```
[IL]: Error [StackUnexpected]: [... : ZSchemeFuzzed.FCls_0::__lambda_5___lambda_9_39(int32)]
[offset 0x00000005][found Int32][expected ref '[ZSchemeFuzzed]ZSchemeFuzzed.FCls_0']
Unexpected type on the stack.
```

The lambda is emitted as a **static** method on `FCls_0` whose body still reads the
field through `ldarg.0` — which in a static method is the `int32` parameter, not
`this`:

```
### ZSchemeFuzzed.FCls_0::__lambda_5___lambda_9_39 System.Boolean *(System.Int32) static=True
  IL_0000: ldarg.0
  IL_0001: ldfld  System.Int32 ZSchemeFuzzed.FCls_0::<F0>k__BackingField
  ...
```

At runtime that reinterprets an `int` as an object reference. In practice it faults
immediately (`NullReferenceException` for a small int), but it is a genuine
memory-safety violation, not a clean throw.

Three surface shapes, one cause — which one appears depends only on what *else* the
lambda captured:

| shape | when | error |
|---|---|---|
| static method, `ldarg.0` | the lambda captures nothing else | `found Int32, expected ref FCls_0` |
| display class, `ldarg.0` | the lambda captures an enclosing local, so a `<>c__` type is built — but with no `<>this` field | `found ref '…+<>c__…', expected ref FCls_0` |
| static method, `ldloc N` | an *enclosing* lambda did capture `<>this` into a local, and the stale `EmitContext` leaks into the nested static lambda | `[UnrecognizedLocalNumber]` at offset 0 |

Second shape:
`issues/repros/lambda-loses-this-when-field-shadows-toplevel-fn.zs` with an extra
`(let ([k 7]) …)` captured by the lambda. Third shape: `fuzz-failure-ffdf0662`,
`fuzz-failure-4f6c199d`.

## Root cause

`IlEmitter.EmitLambda` (`src/ZScheme.Compiler/Codegen/IlEmitter.Emit.cs:3684`)
decides whether a lambda inside a class instance method must capture the enclosing
`this`. It walks the lambda's free variables looking for class-field names
(`:3735-3752`) and, on finding one, adds a synthetic `<>this` capture so that
`EmitLoadClassThis` (`IlEmitter.cs:1353`) can route field access through the
captured local instead of `ldarg.0`.

The loop skips any free var that also names a top-level function
(`IlEmitter.Emit.cs:3748`):

```csharp
if (_methods.ContainsKey(Sanitize(fv)) || _staticFields.ContainsKey(fv))
    continue;
```

The comment above it explains the intent: such a name "resolves to the function at
the call site (EmitCall checks `_methods` first), so the lambda doesn't actually
need `<>this` for it." That reasoning is wrong for a *value* reference. Inside a
class method the field is in bare-name scope and shadows the top-level function —
type inference resolves `(+ x f0)` to the field, and the body emitter duly emits
`ldfld <F0>k__BackingField` via `EmitLoadClassThis`. So the capture analysis and
the body emission disagree about the very same name: one says "that's the function,
no `this` needed", the other emits an instance field read. The verifier catches the
contradiction; the runtime does not.

Note the skip is keyed on `_methods` — a *global*, module-wide registry
(`IlEmitter.cs:691-695`) that is not scoped to the lambda's enclosing class — so any
top-level function anywhere in the module can disable the capture for a field of
that name.

### Why it surfaced now

The check itself dates to `1af4775` (Apr 27, "Skip object-expr capture for top-level
functions shadowed by class fields") and is on `master`. What changed is *when*
`_methods` is populated.

Bisecting the minimal repro across `master..tco-in-classes-and-objects` lands on
`ad1bcbc` ("Let a local function call itself and its siblings with letrec"), the
branch's first commit. It made the IL main-module emitter register **every**
top-level signature before emitting any body
(`IlEmitter.Emit.cs:287-302`), which is exactly what a lifted `letrec` group needs
in order to call a sibling declared later in the file.

Before that change, `_methods` was filled in as emission walked the file, so a class
declared *before* the colliding `(define (f0 …))` was emitted while `_methods` still
had no `F0` entry — the skip did not fire and the correct code came out. The bug was
always there, merely hidden by source order. Confirmed on `master` by moving the
function above the class:

```
=== master, function declared BEFORE the class:
[IL]: Error [StackUnexpected]: [... FCls_0::__lambda_5___lambda_11_39(int32)]
[offset 0x00000005][found Int32][expected ref '…FCls_0']
```

So: pre-existing latent defect, made order-independent (and therefore near-universal)
by `ad1bcbc`. The fuzzer names class fields `f0, f1, …` and top-level functions
`f0, f1, …`, which is why it now trips on ~20% of generated programs.

## Suggested fix direction

The capture analysis has to ask the same question the body emitter answers, rather
than re-deriving it from name registries. Two options, in preference order:

1. Decide from the resolved IR, not the name. If the lambda body contains a
   `FieldGet`/`SetField` (or whatever node the class-field read lowered to) against
   the enclosing class, `<>this` is needed — regardless of what else shares the name.
   `BodyContainsClassFieldSet` (`IlEmitter.cs:~1320`) already walks the body for the
   write half; the read half wants the same treatment.
2. Failing that, scope the skip correctly: it should fire only when the name does
   *not* resolve to a field in the enclosing class's scope. Since a class field
   shadows a module-level function inside the class body, that condition is simply
   never true for a field name — which suggests the `_methods` clause should be
   dropped from this loop and the original `1af4775` symptom (a nested `(object …)`
   constructor capturing the wrong-typed `this`) re-fixed where it actually lives, in
   `EmitObjectExpr`'s free-var resolution.

Either way, the `ldloc` variant needs its own fix: `EmitContext.CurrentClassThisLocal`
must not be inherited by a nested lambda emitted as a separate method — the local it
names belongs to the enclosing method's frame.

Whatever the fix, the C# backend is unaffected (it compiles all three shapes
correctly), so the existing differential oracle will confirm it.

## Priority note

Highest of the three bugs in this run, and the highest-priority open issue in
`issues/` — this is a silent miscompilation, not a rejected program. The IL backend
emits verifiably-invalid code that reinterprets an integer as an object pointer, and
it does so for a program the C# backend compiles correctly and that the type checker
accepts. Any ZScheme program with a class field and a same-named top-level function
is affected, on every commit of this branch, independent of source order.

It is also currently blinding the fuzzer: `ilverify` short-circuits ahead of
`diffexec` (§5 of `docs/FUZZER.md`), so all 220 failing cases in this run were
dropped before the semantic oracle ever ran — `oracle.diffexec.skipped: 220`,
`oracle.diffexec.failed: 0`. Until this is fixed, a fuzz run tells us almost nothing
about execution divergence.
