# A match-arm pattern binder read after an await is silently zeroed (IL backend)

## Symptom

An arm binder that is read *after* an `await` in the same arm comes back as the default
value of its type instead of what the pattern bound. No exception, no diagnostic — just a
wrong answer.

```scheme
(module matchawait)
(import-clr
  [task-delay System.Threading.Tasks.Task/Delay : (Int -> System.Threading.Tasks.Task)])
(define-union Box (Full [v : Int]) (Empty))
(define-async (open-box [b : Box]) : (Task Int)
  (match b
    [(Full v) (begin (await (task-delay 1)) v)]   ; <-- `v` read after the await
    [(Empty) 0]))
(define-async (Compute) : (Task Int) (await (open-box (Full 42))))
```

```
$ zs compile matchawait.zs --backend il -o matchawait.dll
$ # invoke Compute()
0        <-- expected 42
```

**The C# backend is correct here.** It lowers the awaiting arm into an async lambda, so `v`
is captured by the closure and survives:

```csharp
return b switch { Full(var v) => (await ((System.Func<System.Threading.Tasks.Task<int>>)(
    async () => { await System.Threading.Tasks.Task.Delay(1); return v; }))()), Empty => 0, … };
```

So this is both a silent miscompile and a backend divergence.

## It only manifests on a real suspension

Swapping the awaited task for one that is already complete makes it pass:

```scheme
[(Full v) (begin (await (task-completed-task)) v)]   ; => 42, correct
```

`await` on a completed Task never yields, so `MoveNext` runs straight through and the binder
survives in its CIL local. The wrong value appears only when the state machine actually
suspends and resumes — at which point the local has been reinitialised and never restored.

That gap is why this has stayed hidden: most awaits in practice complete synchronously.

## Root cause

Locals that must survive a suspension are hoisted into state-machine fields, and there are
two halves to that:

- `AsyncStateMachineAnalyzer.CollectInfo` collects `HoistedLocals`, from which
  `IlAsyncEmitter.EmitAsyncFuncDef` creates one `<name>5__` field each. It has cases for
  `Let`, `Use` and `with-handlers` binders — **but not for match-arm patterns**.
- `EmitMoveNextAwait` saves/restores `mnCtx.AllLocals` around each suspension point.
  `IlEmitter.EmitLetBinding` registers its local there (and `Stfld`s it); `EmitMatchArms`
  binds pattern variables straight into the `locals` dictionary and registers **nothing**.

So a match binder has no field to be saved into and is not in the save/restore list. On
resume its CIL local is whatever `InitializeLocals` left it — zero.

## Scope

Any `match` arm in an `async` function that awaits and then reads a binder. `Use`- and
`Let`-bound names are fine; only pattern binders are affected.

Pre-existing: reproduced identically on `f04dd655` (before the async-TCO work) and after.
The async-TCO change neither causes nor worsens it — a `TcoJump`'s arguments are evaluated
before the jump — but async TCO makes awaiting `match` bodies more common, so it raises the
odds of someone hitting this.

Live example in a downstream consumer: ZWorld's `fsm-tick-current!`
(`run/scripts/src/lib/fsm.zs:94-107`) binds `st` in the outer match and then reads it after
an await, to call `(FsmState/on-exit st)` on a `Goto` transition. Its tests pass today only
because the awaited NPC handlers all complete synchronously.

## Fix sketch

Mirror what `Let` already does, in both halves:

1. `AsyncStateMachineAnalyzer.CollectInfo`'s `Match` case: record each arm's pattern binders
   (name + type, which `PatternResolver` has already annotated) as `HoistedLocal`s, subject
   to the same `seenLocals` de-duplication. Note the existing `_`-binding caveat in the
   `Let` case — hoisting is keyed by name only, so sibling arms binding the same name at
   different types would alias one field and fail `ilverify`; arms are separate scopes, so
   they need either per-arm naming or the same type-mismatch guard.
2. `IlEmitter.EmitMatchArms`: when `ctx.MoveNextCtx` has a field for the binder, `Stfld` it
   and add it to `AllLocals`, exactly as `EmitLetBinding` (`IlEmitter.Emit.cs:1393-1433`)
   does.

A regression test belongs in `Integration/EndToEndTests.cs` alongside the async TCO cases,
and must await something genuinely incomplete (`Task.Delay`) — a `task-completed-task`
version passes even with the bug present.
