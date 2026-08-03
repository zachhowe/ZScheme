# `letrec` value binding in a constructor's super-args loses a captured local (IL backend)

**Found by:** differential fuzzer, `compile` oracle (one backend succeeded, IL failed).
Reproduces at roughly 1 case in 400–2000 depending on the seed.

**Status:** pre-existing, unrelated to nested defines. Confirmed by a baseline fuzz run with
`GeneratorContext.EnableNestedDefines` forced off: the failing source contained zero nested
defines and produced the identical signature (`fuzz-failure-d3eae386`, seed `0x3ef61ebc`). The
code path involved — `LetrecLifter.LiftGroup`'s `functionNames.Count == 0` early return — is
untouched by the nested-define work.

## Symptom

```
Error: Variable 'x17' not found for AsmResolver IL emission
```

The C# backend compiles the same program fine; only the IL backend fails, so it trips the
`compile` oracle rather than `ilverify` or `diffexec`.

## Minimal repro

```scheme
(module test)

(define-class #:open Base
  [b : Int]
  (define (Get) : Int b))

(define (f0 [x17 : Int]) : Int
  (let ([o (object : Base
             (constructor (super (letrec ([v x17]) v)))
             (define (M) : Int 1))])
    (Base/Get o)))

(define (compute) : Int (f0 7))
```

```
zs compile repro.zs -b il -o out.dll
  Error: Variable 'x17' not found for AsmResolver IL emission
```

## What narrows it down

Holding everything else fixed and varying only the super-argument:

| Super-argument | IL backend |
|---|---|
| `x17` | OK |
| `(let ([v x17]) v)` | OK |
| `((lambda ([n : Int]) (+ n x17)) 1)` | OK |
| `(letrec ([v x17]) v)` | **fails** |

So it is not "reading a captured local in a super-arg" in general — a plain `let` spine over the
same value is fine. It is specific to the `let` spine that `LetrecLifter` *synthesizes*. Note the
group has **no function bindings at all**, so no lifting happens; the group is just rewritten into
a `let` spine by `BuildSpine`.

## Where to look

- `ObjectLifter` turns the `(object …)` into a synthesized class and each captured local into
  both a constructor parameter and a field; inside the constructor the name refers to the
  *parameter*. `LetrecLifter.RewriteConstructor`
  (`src/ZScheme.Compiler/Ir/LetrecLifter.cs`) binds those parameter names into `ctorScope`, so the
  lifter itself is happy.
- `LetrecLifter.BuildSpine` constructs `new IrNode.Let(name, value, body, varType)` directly. It
  sets `Type` and `Span` from the originating `LetRec` but leaves `EmitName` null, whereas a
  source-level `let` gets one from `EmitNameResolver`. The working-vs-failing table above points
  at that difference — most likely the IL emitter resolves the binding through a name the
  synthesized `Let` never got, and the super-args emission path (which runs before `this`
  exists, so it has its own argument handling) is where it shows up.
- Compare `IlEmitter`'s constructor/super-args emission against its ordinary method-body path for
  how a `Let` binding's slot is registered.

## Suggested fix direction

Either have `BuildSpine` carry the `EmitName` its bindings need, or re-run `EmitNameResolver`
after `LetrecLifter` so synthesized bindings are named on the same footing as source ones. Add a
dual-backend case to `tests/ZScheme.Compiler.Tests/Integration/LetrecTests.cs` alongside the
existing `LetrecInClassMethod_*` pair — a `letrec` in a constructor's super-args, both with and
without function bindings.
