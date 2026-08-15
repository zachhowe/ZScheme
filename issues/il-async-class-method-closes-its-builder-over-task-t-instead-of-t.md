# An async class method closes its builder over `Task<T>` instead of `T` (IL backend)

**Found by:** hand-reduced from a downstream failure in ZWorld, immediately after `4df0f07`
fixed the adjacent bare-name erasure. Reproduced against `4df0f07`.

**Adjacent to `4df0f07` but distinct.** That commit fixed the `define-async` unwrap reading
`ReturnTypeAnnotation` instead of the inferred type, so a bare CLR name reached codegen
unresolved. This is the *other* half of the same unwrap: for a **class method**, the `(Task T)`
annotation is not unwrapped at all. Fixing the first exposed the second — ZWorld went from 26
failures to 6, and the 6 are all class methods.

**Affects:** any `define-async` **class method** whose body contains an `await` and whose result
is a CLR value type. Module-level async functions are correct.

## The defect

The state-machine builder is closed over the whole task type rather than its result. From
`cls4.dll`, reflecting on the emitted state machines in one assembly:

| state machine | declared by | `__builder` field type |
|---|---|---|
| `<Compute>d__1` | module-level `(Task Int)` | `AsyncTaskMethodBuilder`1[System.Int32]` ✅ |
| `<Get>d__0` | class method `(Task System.Guid)` | **`AsyncTaskMethodBuilder`1[Task`1[System.Guid]]`** ❌ |

The stub's declared return type is correct — `Holder.Get -> Task<System.Guid>` — so the method
signature and its own builder disagree. `SetResult` is handed the `T` the body produced while
the builder expects a `Task<T>`.

That is why the severity tracks reference-vs-value:

- **T is a reference type — works by accident.** `SetResult` stores a pointer where a pointer is
  expected. The `Task` handed back is really a `Task<Task<Box>>`, and the awaiting caller reads
  the same pointer back out and treats it as `Box`. Wrong metadata, right bits.
- **T is a value type — broken.** A 16-byte `Guid` (or any imported struct) is stored where a
  reference is expected.

## Repro

```
dotnet run --project src/ZScheme.Fuzzer -- \
  --repro issues/repros/async-class-method-double-wraps-its-task-builder.zs
```

```
[compile] PASS: ok
[il-run] returned 8
[diffexec] FAIL: Compute() return diverged (IL=8, CS=7)
```

Self-contained — `System.Guid` is enough, no referenced assembly needed.

```scheme
(define (expected) : System.Guid
  (guid-parse "11111111-1111-1111-1111-111111111111"))

(define-async (leaf) : (Task Int) 1)

(define-class Holder
  [seed : Int]
  ;; contains an await and returns a CLR value type
  (define-async (Get) : (Task System.Guid)
    (begin (await (leaf)) (expected))))

(define-async (compute) : (Task Int)
  (let ([g (await (Holder/Get (Holder 1)))])
    (+ 7 (guid-cmp g (expected)))))   ; 7 if g survived; IL returns 8
```

## Three symptoms, one cause

Which one you get depends on what the corrupted slot is subsequently used for. All three are the
same defect; the value-typed result is the constant.

| shape (class method) | symptom |
|---|---|
| awaits `Task<Int>`, returns `Guid` | silently wrong value — the repro above |
| awaits `Task<S>`, returns that `S` (struct with a `string` field) | `NullReferenceException` |
| awaits `Task<C>` (reference), returns an `S` built inline | **`AccessViolationException`** — kills the test host |

The `AccessViolationException` reproduces in isolation, not only in combination.

## What is not affected

Verified against `4df0f07`, all in one package with a `(ref …)` assembly supplying a
`readonly record struct Ans(bool Ok, string Line)` and a `sealed record Box(string Line)`:

| shape | result |
|---|---|
| module-level async, awaits `Task<Ans>`, returns it | pass |
| module-level async, awaits `Task<Ans>`, returns `Int` | pass |
| class method, **no await**, returns `Ans` | pass |
| class method, awaits `Task<Box>`, returns `Box` | pass (see "by accident" above) |
| class method, awaits `Task<Ans>`, returns `Int` | pass |
| class method, awaits `Task<Ans>`, returns `Ans` | **fail** |

So it needs all three: a class method, an `await` in the body, and a value-typed result. A class
method with no await completes synchronously and never builds the broken state machine.

**Not caught by `ilverify`** — same as the last one, the assembly verifies clean.

## Root cause

`IlAsyncEmitter.cs:117-119` closes the builder over `_host.MapToClr(func.ReturnType, ctx)`:

```csharp
var builder = isVoid
    ? MakeTypeRef(typeof(AsyncTaskMethodBuilder), null)
    : MakeTypeRef(typeof(AsyncTaskMethodBuilder<>), _host.MapToClr(func.ReturnType, ctx));
```

This is correct *given* the invariant the comment above it assumes — that `func.ReturnType` for
an async function is the already-unwrapped result type. `4df0f07` is what establishes that
invariant, in the IR-lowering unwrap. The evidence above says the class-method path does not go
through it: `<Compute>d__0` gets `Int32`, `<Get>d__0` gets `Task<Guid>`.

The same `func.ReturnType` is used for the result local at `:367`, so both are wrong together,
which is consistent with the value simply never landing where the caller reads it.

**I did not confirm which lowering path class methods take** — only that whatever it is does not
apply the unwrap that `4df0f07` fixed for module-level functions. That is the thing to look at
first, and the fix is plausibly to route both through one place rather than patching a second
site.

**Object expressions are unverified.** `(object IFace …)` methods are emitted by a related path
and may well have the same gap; the ZWorld code that would exercise it (`dialogue-nodes.zs`) has
no async value-typed method, so nothing there disproves it.

## Priority note

Worse than the bug it was hiding behind. That one failed loudly at JIT time; this one has a
silent-wrong-answer mode, and the two crash modes it does have (`NullReferenceException`,
`AccessViolationException`) point nowhere near an async return type. An `AccessViolationException`
in particular is uncatchable by default and takes the host process with it, which in a test run
means the whole suite dies rather than one case failing.

It is also newly reachable: `4df0f07` made bare CLR names in `(Task T)` resolve, so code that
previously died at JIT now compiles to a correct signature over a broken state machine.

Downstream, this is what blocks ZWorld: six behavior tests, all of them
`OnCommandRequestedAsync` — an async class method implementing a CLR interface, returning
`Task<NpcCommandAnswer>` where `NpcCommandAnswer` is a `readonly record struct`, whose body
awaits. Its module-level counterparts in `commands.zs` all pass.

**No workaround** short of making the result a reference type, which is what the downstream
project had done by accident and has now reverted.
