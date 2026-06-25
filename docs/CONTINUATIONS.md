# First-class continuations in ZScheme

ZScheme supports Scheme's `call/cc` (call-with-current-continuation) for capturing the
"rest of the program" as a callable value. Implementation follows **"Continuations from
Generalized Stack Inspection"** (Pettyjohn, Clements, Marshall, Krishnamurthi, Felleisen,
ICFP 2005), which uses .NET exceptions and synthesized frame objects rather than the native
runtime stack.

## The form

```scheme
(call/cc f)
```

`f` is a function of one argument. ZScheme types `call/cc` as `∀α∀β. ((α → β) → α) → α`:

- `α` is the type the call/cc form yields.
- The argument `f` receives a unary continuation `k : α → β`.
- `β` is universally polymorphic because invoking `k` aborts and never returns.

If `f` returns a value `v` normally, the form yields `v`. If `f` calls `(k v)`, the form
yields `v` *as if call/cc had returned directly with `v`*.

## How it works

The transformation has two parts:

### 1. Front end (parse, type-check, lower)

`(call/cc f)` is recognized as a special form in
[`AstBuilder.cs`](../src/ZScheme.Compiler/Ast/AstBuilder.cs) and lowered in
[`IrLowering.cs`](../src/ZScheme.Compiler/Ir/IrLowering.cs) to a call to
`ZScheme.Runtime.Runtime.CallCcTyped(f)`. C# infers the generic type arguments from `f`'s
static type.

### 2. Capturable-call hoister

[`CapturableCallHoister.cs`](../src/ZScheme.Compiler/Ir/CapturableCallHoister.cs) runs
just before `ContinuationTransform` (gated by the same `ProgramUsesCallCc` check).
It A-normalizes every value-consuming sub-expression position so that any
continuation-capturing runtime call (`CallCcTyped`, `ShiftTyped`, `ControlTyped`,
`CallCompTyped`, `Reset`, and tagged variants) ends up as the immediate value of a
`Let`. Without this pass, a call appearing inside a `BinOp`, `Call` argument, record
field, etc. would have its surrounding context dropped from the captured continuation
because `ContinuationTransform` only attaches a frame at let-value boundaries.

The hoister walks the IR. For each value-consuming position whose operand contains a
capturable call (BinOp operands, Call/MethodCall args and receivers, ClrCall/ClrNew
args, TupleNew/RecordNew/UnionCaseNew/MutableArrayNew elements, FieldGet record,
RecordWith record/updates, TypeTest value, Cast operand, Throw expr, Await expr,
SetField value, UnaryOp operand, SuperMethodCall args, Match scrutinee, If condition)
the operand is lifted into a fresh `Let` (`__cc_hoist_N`) so the call's post-call
context is the let body. To preserve left-to-right evaluation order, *all* non-trivial
earlier operands are also bound; trivial leaves (Var, literal) stay inline. After
hoisting, the pipeline re-runs `TailCallAnalyzer` because the new `Let`s change which
calls sit in tail position.

Two positions are deliberately *not* hoisted:

- Branches of `If` and arms of `Match` — only one fires per execution; lifting them
  would force unconditional evaluation. They are handled by the extension to
  `IsCapturable` described below.
- Operands of short-circuit `and` / `or` — the right operand is conditional. Capturing
  context through these remains a known limitation (see "Status of v1 limitations").

### 3. Continuation transform (the interesting part)

[`ContinuationTransform.cs`](../src/ZScheme.Compiler/Ir/ContinuationTransform.cs) runs
when the program contains any continuation-capturing operator. It rewrites every
non-tail call inside a *capturable* function so the call site can save its state if a
`SaveContinuation` exception flies past it.

The pass dispatches on `Let` whose value `IsCapturable`. Originally that meant a
direct non-tail `Call` or `ClrCall`; the predicate now also matches compound shapes
whose tail-yielding sub-positions transitively contain a non-tail call:

- `If` whose `Then` or `Else` is capturable (covers `(let r (if cond (call/cc …) other) post)`).
- `Match` whose any arm body is capturable.
- `Seq` whose last node is capturable.
- nested `Let` whose value or body is capturable.
- `WithHandlers` whose body is capturable.

This pairs with the hoister: once value-consuming positions have been A-normalized,
the only remaining place a capturable call can sit (besides the immediate let-value)
is a tail-yielding sub-position of a compound let-value, and wrapping the entire
compound is correct — its result type matches the let-binding's expected type.

For each capturable `Let(t, value, body)` in a capturable function, the pass:

1. Lifts `body` into a sibling continuation function `__cont_<func>_<n>(t, ...live)` whose
   parameters are `t` plus the variables `body` references from the enclosing scope.
2. Synthesizes a frame class `__Frame_<func>_<n>` implementing `ZScheme.Runtime.IFrame`.
   The frame holds the captured live variables in fields and exposes
   `object Invoke(object returnValue)` that calls the continuation function, casting
   `returnValue` back to `t`'s static type.
3. Wraps the original let-value in `with-handlers`. The handler catches
   `ZScheme.Runtime.SaveContinuation`, appends a fresh frame instance to its frame list,
   and rethrows.

The transform is recursive — `__cont_<func>_<n>`'s body itself runs through the same
pass, so nested non-tail calls each get their own frame.

### 4. Runtime

[`src/ZScheme.Runtime/`](../src/ZScheme.Runtime) ships with the compiler. Its DLL is
auto-referenced from the generated csproj whenever the program uses `call/cc`.

| Type / Method | Role |
|---------------|------|
| `SaveContinuation : Exception` | Carries the in-flight frame list and the user-supplied `f`. |
| `IFrame` | One per captured call site. `Invoke(rv)` resumes the post-call computation. |
| `Continuation` | The reified `k` passed to `f`. `Invoke(v)` throws `AbortAndResume(frames, v)`. |
| `Runtime.CallCcTyped<T,U>(userFn)` | Throws `SaveContinuation`. The throw is what triggers capture. |
| `Runtime.Resume(frames, value)` | Replays a captured frame list, threading `value` through. |
| `Runtime.ResumeAsync(frames, value)` | Async sibling — awaits each frame's `InvokeAsync` so async-tail frames don't block the dispatch loop. |
| `Runtime.Run<T>(programMain)` | Top-level driver — catches `SaveContinuation` / `AbortAndResume` and dispatches. |
| `Runtime.RunAsync<T>(programMain)` | Async top-level driver — same loop as `Run`, but uses `ResumeAsync` so programs synthesizing async frames replay without blocking. |

The dance for `(call/cc (lambda (k) (k 41)))` inside a `Let`:

1. User code calls `Runtime.CallCcTyped(adapter)` which throws `SaveContinuation`.
2. The catch around the let-binding extends the exception with a frame for the post-call
   work, then rethrows.
3. `Runtime.Run`'s catch catches the fully-built `SaveContinuation`, builds a
   `Continuation` from its frames, and calls user's `f` with it.
4. User's `f` calls `(k 41)`, throwing `AbortAndResume(frames, 41)`.
5. `Runtime.Run` catches `AbortAndResume` and replays the captured frames with `41` as the
   seed — each frame's `Invoke` runs the post-call work for its call site.

## Running call/cc programs

User programs that capture a continuation must be invoked through `Runtime.Run`, which
establishes the topmost catch:

```csharp
// host program (C#)
var result = ZScheme.Runtime.Runtime.Run(() => MyZSchemeModule.UseCallcc());
```

Without `Run`, an escaping `SaveContinuation` or `AbortAndResume` will terminate the
process.

For programs that capture continuations inside `[async]` functions whose post-call code
crosses an `await`, the synthesized continuation function is async and the frame class
exposes `InvokeAsync`. Drive those programs through `Runtime.RunAsync` instead so the
replay path awaits each frame rather than blocking on its underlying `Task`:

```csharp
// host program (C#) — async-tail program
var result = await ZScheme.Runtime.Runtime.RunAsync(() => MyZSchemeModule.UseAsyncCallcc());
```

`RunAsync` and `Run` share the same throw / catch / replay loop; the only difference is
which `Resume` variant they call. Sync frames work in both — the default `IFrame.InvokeAsync`
implementation wraps `Invoke` in a completed task. Programs that don't synthesize any
async frames can keep using `Run`.

## Examples

See [`examples/call-cc.zs`](../examples/call-cc.zs) for runnable demonstrations of basic
capture, early exit (escape continuation), free-variable capture, and nested `call/cc`.

See [`examples/async-callcc.zs`](../examples/async-callcc.zs) and
[`examples/async-shift-reset.zs`](../examples/async-shift-reset.zs) for continuation
capture inside `[async]` functions — including patterns where the captured continuation's
tail crosses an `await`.

## Delimited continuations

ZScheme exposes both Danvy–Filinski (`shift`/`reset`) and Felleisen
(`control`/`prompt`) styles, plus Racket's `call-with-composable-continuation`
(`call/comp`) and first-class prompt tags. They all reuse the same
`SaveContinuation`/frame-synthesis machinery as `call/cc`; the difference is *where*
capture is bounded, whether the reified continuation reinstalls a prompt on resume,
and which prompt a capture targets.

### Forms

```scheme
;; Default-tagged delimiters
(reset e)               ; Danvy/Filinski prompt; result type = type of e
(prompt e)              ; alias of (reset e); paired idiomatically with (control)

;; Default-tagged captures
(shift k e)             ; D/F: captured k reinstalls a fresh prompt on resume
(control k e)           ; Felleisen: captured k composes WITHOUT a fresh prompt
(call/comp f)           ; Racket: (call/comp f) ≡ (control k (f k))

;; Tagged variants — target a specific (matching) prompt
(reset tag e)
(prompt tag e)
(shift tag k e)
(control tag k e)
(call/comp f tag)

;; First-class prompt tags
(make-prompt-tag)       ; allocates a fresh PromptTag at runtime
```

`prompt` is a pure surface alias for `reset`; both lower to the same runtime call.
The tagged-arity overload distinguishes `(reset e)` / `(prompt e)` from
`(reset tag e)` / `(prompt tag e)` purely by argument count.

### Typing rules

HM with an answer-type stack `Σ`. Tags are non-generic at the type level (matching
Racket): the type inferer expects every tag expression to unify with `PromptTag`.

```
Γ ⊢ e : τ        (with Σ extended by fresh α; α unified with τ)
─────────────────
Γ; Σ ⊢ (reset e) : τ        Γ; Σ ⊢ (prompt e) : τ

Γ ⊢ tag : PromptTag    Γ ⊢ e : τ        (Σ as above)
─────────────────
Γ; Σ ⊢ (reset tag e) : τ    Γ; Σ ⊢ (prompt tag e) : τ

Γ, k : α → τ ⊢ e : τ      (τ = top(Σ))
─────────────────
Γ; Σ ⊢ (shift k e) : α      Γ; Σ ⊢ (control k e) : α

Γ ⊢ tag : PromptTag    Γ, k : α → τ ⊢ e : τ      (τ = top(Σ))
─────────────────
Γ; Σ ⊢ (shift tag k e) : α     Γ; Σ ⊢ (control tag k e) : α

Γ ⊢ f : (α → τ) → τ        (τ = top(Σ))
─────────────────
Γ; Σ ⊢ (call/comp f) : α    Γ; Σ ⊢ (call/comp f tag) : α
                            (the tagged form additionally requires tag : PromptTag)

─────────────────
Γ ⊢ (make-prompt-tag) : PromptTag
```

`_resetAnswerTypes` is a flat stack: tagged operators conservatively read the
**innermost** answer type regardless of tag, because tags are runtime values that may
be aliased through bindings, and static tag→answer-type tracking would be unsound. A
mismatch (capture targeting a tag that isn't on the dynamic stack) surfaces at
**runtime** as an `InvalidOperationException` — same behavior as Racket.

### Resume semantics: `shift` vs. `control`

The two operators differ only in what `Invoke(v)` does on the reified continuation:

- **`shift` / `(call/comp f)` is *not* used here** — see below. The captured `k` is a
  `DelimitedContinuation<TIn,TAns>` (or `TaggedDelimitedContinuation` for `shift tag`)
  whose `Invoke(v)` re-runs the captured frames inside a *fresh prompt* of the same
  (matching) tag. Nested shifts during the resumption are scoped to that fresh prompt.
- **`control` / `call/comp`** reify their continuation as a
  `ComposableContinuation<TIn,TAns>` whose `Invoke(v)` calls `Resume(frames, v)`
  directly, with **no fresh prompt**. Nested `(control)` / `(shift)` inside the
  resumed frames therefore search outward through whatever prompt is *currently* on
  the dynamic stack — they merge into the caller's context.

This is the standard Danvy/Filinski vs. Felleisen distinction. Both are equally
expressive but produce different values for nested-capture programs.

`call/comp` desugars to `(control k (f k))` and shares its resume semantics — i.e.
the captured continuation composes Felleisen-style.

### Tagged prompts

Each `(make-prompt-tag)` call allocates a distinct `PromptTag` (reference identity).
A tagged capture (`shift tag …`, `control tag …`, `call/comp f tag`) walks past
prompts with non-matching tags until it finds one that matches. Untagged
(default-tag) `shift` / `control` always target the innermost prompt regardless of
its tag — the dynamic-tag escape mechanism is only available through the tagged
forms.

The runtime implementation:

- `Runtime.ResetAt(tag, body)` pushes the user-supplied tag onto `PromptStack`
  instead of allocating a fresh one.
- Tagged capture operations validate the tag is on the stack (`PromptStack.Contains`)
  before throwing, so a missing-tag capture produces a clear error rather than an
  opaque escaped throw.
- Default-tagged `Reset` / `ShiftTyped` keep using fresh tags and the
  `PromptStack.Peek()` fast path — no behavior change for existing code.

### Tag-mismatch is a runtime error

Static tag tracking would force every operator to thread an answer-type-indexed
`PromptTag<τ>` (and prevent perfectly reasonable patterns like passing a tag through
a polymorphic field). We deliberately keep `PromptTag` a non-generic type and rely on
the `PromptStack.Contains` runtime check, matching Racket. The diagnostic message is
`"… target prompt-tag is not on the dynamic prompt stack."`.

### Examples

- [`examples/shift-reset.zs`](../examples/shift-reset.zs) — Danvy/Filinski operators.
- [`examples/control-prompt.zs`](../examples/control-prompt.zs) — Felleisen operators,
  `call/comp`, tagged prompts, `make-prompt-tag`.

## Multi-value continuations

A continuation captured by any of the capture forms can be invoked with multiple
values: `(k v1 v2 … vn)` for `n ≥ 2` is sugar for `(k (values v1 v2 … vn))`,
and the surrounding capture form yields the tuple `(τ₁, …, τₙ)`. Producer side:

```scheme
;; (call/cc f) yields whatever k receives — here, a (Int * Int * Int) triple.
(call/cc (lambda (k) (k 1 2 3)))             ; ⇒ (1, 2, 3)

;; Same shape across (shift)/(reset), (control)/(prompt), (call/comp).
(reset (shift k (k 7 8)))                ; ⇒ (7, 8)
(prompt (control k (k 100 200)))         ; ⇒ (100, 200)
(prompt (call/comp (lambda (k) (k 1 2 3))))  ; ⇒ (1, 2, 3)
```

Consumer side — destructure with `let-values` or `call-with-values`:

```scheme
;; Pattern-style destructuring.
(let-values ([(a b c) (call/cc (lambda (k) (k 1 2 3)))])
  (+ a (+ b c)))                              ; ⇒ 6

;; Producer/consumer pairing.
(call-with-values
  (lambda () (call/cc (lambda (k) (k 1 10 100))))
  (lambda (a b c) (+ a (+ b c))))                 ; ⇒ 111
```

Auto-bundling is scoped to *continuation parameters* — the bindings introduced
by `(call/cc (lambda (k) …))`, `(shift k …)`, `(control k …)`, `(call/comp (lambda (k) …))`
and their tagged variants. The marker propagates through trivial `(let [k2 k] …)`
rebindings; passing `k` into a helper function or storing it in a record field
loses the marker, so those uses must call as `(k (values v1 v2 …))` explicitly.

Single-value `(k v)` is unchanged: the existing α type and direct-call codegen
are preserved bit-for-bit. Mixed-arity invocations of the same `k` (some `(k v)`,
some `(k v1 v2)`) are a type error — α can't simultaneously be `T` and
`Tuple[T, T]`.

Implementation: the inferer rewrites `(k v1 v2 … vn)` to `(k (TupleNew v1 … vn))`
via `Apply.RewrittenArgs` — a single-arg call with a tuple argument. The runtime
layer (`Continuation`, `IFrame`, `Resume`, `CallCcTyped<T,U>`) is **untouched**:
the value carried through frames is just a `ValueTuple<…>` reference, indistinguishable
from any other reified tuple. The 7-element arity cap matches the existing
`values` ceiling.

`let-values` and `call-with-values` desugar at AST-build time:

- `(let-values ([(x y z) expr]) body)` for `n ≥ 2` becomes
  `(match expr [(values x y z) body])`. Arity 1 collapses to a plain `(let [x expr] body)`.
- `(call-with-values producer-thunk consumer-fn)` requires `consumer-fn` to be a
  literal `(lambda (a1 … an) body)`; it expands to `(let [_ (producer-thunk)] …)`
  with each `ai` bound to the matching tuple slot. Non-literal consumers are
  rejected — the consumer's arity must be statically known to pair with the
  producer's tuple shape.

## Status of v1 limitations

These were the limitations of the initial prototype. Several have since been
lifted; the remaining ones are tracked here.

### Resolved

- ~~**First-class prompt tags are not exposed.**~~ `(make-prompt-tag)`,
  `(reset tag e)`, `(prompt tag e)`, `(shift tag k e)`, `(control tag k e)`, and
  `(call/comp f tag)` are all surfaced. `PromptTag` is a first-class user-visible
  type.
- ~~**`call/comp` and Felleisen's `control`/`prompt` not yet provided.**~~
  `(prompt …)`, `(control k …)`, and `(call/comp f)` (plus tagged variants) are
  exposed. `prompt` is a surface alias for `reset`; `control` and `call/comp` use a
  new `ComposableContinuation<TIn,TAns>` that resumes without re-installing a
  prompt.
- ~~**Single-value continuations only.**~~ `(k v1 v2 … vn)` is now supported for
  every capture form (`call/cc`, `shift`, `control`, `call/comp`, plus tagged
  variants). Multi-value invocations are *tuple-based*: the inferer auto-bundles
  the args into a single `(values v1 v2 … vn)` argument, so the continuation type
  becomes `Tuple[τ₁..τₙ] → β` and `(call/cc f)` yields the tuple. The new
  `(let-values ([(x y z) expr]) body)` and `(call-with-values producer consumer)`
  forms destructure the tuple on the consumer side. Auto-bundling is scoped to
  bindings introduced by the capture forms (and trivial `(let [k2 k] …)`
  rebindings); regular tuple-arg functions still require an explicit `(values …)`.
  The 7-element ceiling matches the existing `values` arity cap. See the
  "Multi-value continuations" section below.
- ~~**Capturing context only fires around `(let v <call> body)` shapes.**~~ The
  [`CapturableCallHoister`](../src/ZScheme.Compiler/Ir/CapturableCallHoister.cs)
  pre-pass A-normalizes every value-consuming sub-expression position (BinOp,
  Call args, MethodCall, ClrNew, TupleNew/RecordNew/UnionCaseNew/MutableArrayNew,
  FieldGet, RecordWith, TypeTest, Cast, Throw, Await, SetField, UnaryOp,
  SuperMethodCall, Match scrutinee, If condition) before
  `ContinuationTransform` runs, so any continuation-capturing call appearing as
  a sub-expression of those positions is lifted into a fresh `Let`-binding and
  its surrounding context is captured automatically. Branches of `If` / `Match`
  arms are handled by an extension to `IsCapturable` that recurses into
  tail-yielding positions of compound let-values. Affects all capture forms.
  Two carve-outs remain (see Remaining below): short-circuit `and`/`or`
  operands, and the existing precompiled-assembly callback restriction.
- ~~**Continuation capture inside `[async]` functions is rejected at compile time.**~~
  Top-level `define-async` functions can now use any continuation operator
  (`call/cc`, `shift`, `reset`, `prompt`, `control`, `call/comp`, plus tagged
  variants) anywhere in their body, including positions where the post-call code
  crosses an `await`. `ContinuationTransform` tracks whether the enclosing
  function is async; when the synthesized `__cont_<func>_<n>` body contains an
  `Await`, the cont function is marked `IsAsync = true`, the parent body's tail
  call to it is wrapped in `Await`, and the synthesized frame class implements
  `InvokeAsync` (returning `Task<object>`) in addition to a sync `Invoke` that
  throws `NotSupportedException`. `IFrame` ships a default `InvokeAsync`
  implementation that wraps `Invoke` in a completed task, so sync frames need
  no changes. The runtime exposes `Runtime.ResumeAsync` and `Runtime.RunAsync`
  siblings — programs that synthesize at least one async frame must enter
  through `RunAsync` so the dispatch loop awaits frames instead of blocking.
  `AsyncContinuationAnalyzer` is now narrowed: it only rejects continuation
  operators inside async **methods** on object/class declarations, where the
  surrounding `ClassDecl` / `ObjectExpr` body isn't reached by
  `ContinuationTransform` (see "Remaining" below).

### Remaining

- **Capturing across precompiled-assembly callbacks is rejected only when the
  callee's source is unavailable.** Applies to all of `call/cc`, `shift`, `reset`,
  `prompt`, `control`, and `call/comp`. `ContinuationTransform` only wraps non-tail
  call sites in modules currently being compiled. Functions shipped in precompiled
  `.dll`s (notably the stdlib — `list/map`, `vector/fold`, etc.) have no such
  wrappers, so a `SaveContinuation` exception that flies through them silently
  skips their stack frames and the captured continuation is corrupted. To prevent
  silent misbehavior, [`CrossAssemblyCallCcAnalyzer`](../src/ZScheme.Compiler/Ir/CrossAssemblyCallCcAnalyzer.cs)
  runs after IR lowering and rejects programs where a callback that may capture a
  continuation (directly or through a transitive call to another user function that
  does) is passed to a precompiled higher-order function. The check fires for inline
  lambdas (lifted into closures) and for named user functions referenced by `Var`.
  It does not fire for opaque function-valued arguments (e.g. a callback fetched
  from a record field) — those are a documented blind spot until the proper "safety
  marks" (per the paper) land at runtime.

  This is now usually preventable rather than fatal: see
  [Cross-package continuation capture](#cross-package-continuation-capture). When
  source is unavailable for the precompiled callee, the rejection still fires;
  fall back to hand-rolled recursion or a `:local` dependency on the offending
  package.
- **Continuation capture inside async object/class methods is rejected at compile time.**
  Applies to all of `call/cc`, `shift`, `reset`, `prompt`, `control`, and `call/comp`.
  `ContinuationTransform` walks top-level `FuncDef`s and nested `FuncDef`s inside
  their bodies, but does not recurse into `IrNode.ClassDecl` or `IrNode.ObjectExpr`
  method bodies. A non-tail call to a continuation operator in an async method
  body would therefore not be wrapped with a `SaveContinuation` handler, the
  synthesized frame for that method's post-call code wouldn't be added, and the
  captured continuation list would be missing those frames — silently corrupting
  resumption.
  [`AsyncContinuationAnalyzer`](../src/ZScheme.Compiler/Ir/AsyncContinuationAnalyzer.cs)
  runs after IR lowering and now reports only this narrowed case: an async method
  on a `ClassDecl` or `ObjectExpr` whose body contains an `await` and that
  directly uses a continuation operator (or transitively calls a user function
  that does). Async methods without `await` lower to a plain method that wraps
  the result in `Task.FromResult`, so frame capture remains safe and they are
  not rejected. Top-level `[async]` functions are fully supported (see Resolved
  above); only methods on object/class declarations remain restricted. Workaround:
  factor the continuation operator into a top-level `define-async` and call it
  from the method.
- **Tag-prompt mismatch is detected at runtime, not statically.** A tagged capture
  whose tag isn't on the dynamic stack throws `InvalidOperationException` at the
  capture site (with a clear message). Static detection would require typing tags as
  `PromptTag<τ>` and forbidding tag aliasing through user code; we trade off type
  precision for usability, matching Racket.
- **Short-circuit `and`/`or` operands are not hoisted.** `CapturableCallHoister`
  refuses to A-normalize either operand of `(and …)` / `(or …)` because lifting
  the right operand into a `Let` would force unconditional evaluation, defeating
  short-circuit semantics (matches `WithHandlersHoister`'s carve-out for the same
  reason). A capture inside a short-circuit operand still compiles and runs, but
  its surrounding `(and/or … _ …)` context is dropped from the captured
  continuation. Workaround: bind the call into a `let` outside the `and`/`or`.
- **Code-size cost.** Each non-tail let-bound call in a capturable module compiles
  to a try/catch block plus a synthesized frame class. Programs that don't use any
  continuation operator pay no overhead — `ContinuationTransform` only runs when at
  least one of `call/cc`, `Reset`, `ResetAt`, `ShiftTyped`, `ShiftTypedAt`,
  `ControlTyped`, `ControlTypedAt`, `CallCompTyped`, or `CallCompTypedAt` is
  reachable.
- **TCO interaction.** Tail calls are inherently safe (no frame to capture), and
  `TcoJump` nodes (the IR's tail-self-recursion form) are never wrapped.

## Cross-package continuation capture

`ContinuationTransform` only runs over modules in the *current* compilation. Code
shipped in a precompiled `.dll` was lowered without it, so passing a
continuation-capturing callback into a precompiled higher-order function would
corrupt the captured continuation (see "Remaining" above). To avoid that
limitation without giving up packaged stdlib distribution, the compiler can
re-source a precompiled package on demand:

1. **Bundle source at install time.** A package author opts in by adding
   `(bundle-source true)` to the package manifest. When the package is built
   and cached (`zs install` or auto-install), the cache writes the `.zs` files
   alongside the `.dll`:
   ```
   ~/.zscheme/cache/pkg/<compiler-version>/<pkg>/<ver>/
     <pkg>.dll
     <pkg>.metadata.json     # records `bundleSource: true` and per-module relative paths
     src/
       <module-part>.zs      # import-prefix stripped, so the dir is itself a packagePath root
   ```
   The stdlib (`packages/stdlib/package.zspkg`) sets `(bundle-source true)`, so
   re-installing it makes the source available to consuming compilations.

2. **Auto-route through source when continuations are in use.** Before any
   precompiled package is loaded, `Compilation.MaybeSwapPrecompiledForSource`
   scans the parsed s-expressions for a continuation operator
   (`call/cc`, `reset`, `shift`, `prompt`, `control`, `call/comp`). If any are
   found *and* a cached package ships bundled source *and* the user hasn't
   overridden the package path themselves, the bundled `src/` dir is registered
   as `PackagePaths[importPrefix]`. The existing source-compile path then
   handles that package end-to-end, so `ContinuationTransform` runs over its
   functions and `CrossAssemblyCallCcAnalyzer` no longer sees a precompiled
   boundary to reject across.

3. **Local dependencies are already safe.** A package referenced via
   `[name :local "../path"]` is resolved by `ZSchemeDependencyResolver` into a
   module search path, so its modules are source-compiled into the consuming
   assembly directly. No precompiled boundary, no swap needed.

The unit of substitution is module-level rather than function-level: when a
package gets routed through source, the whole package is re-compiled into the
current assembly. That's a deliberate trade-off — selectively re-lowering
individual functions out of context would require re-running type inference
per-symbol against the rest of the package's exports, which is far more
invasive and brittle. The cost is a one-time recompile of the affected package
on builds that use continuations; programs without continuation operators see
no change.

If a precompiled callee's source is genuinely unavailable (no `bundle-source`,
not a local dep), `CrossAssemblyCallCcAnalyzer` still rejects the program with
a hint pointing at the manifest flag and the `:local` workaround. See
[`Compilation.ContinuationSourceSwap.cs`](../src/ZScheme.Compiler/Pipeline/Compilation.ContinuationSourceSwap.cs).

## Reference

- Pettyjohn, G., Clements, J., Marshall, J., Krishnamurthi, S., Felleisen, M.
  "Continuations from Generalized Stack Inspection." *ICFP 2005*.
- Danvy, O., Filinski, A. "Abstracting Control." *LFP 1990*.
- Felleisen, M. "The Theory and Practice of First-Class Prompts." *POPL 1988*.
- Racket's [`racket/control`](https://docs.racket-lang.org/reference/cont.html) and
  `call-with-composable-continuation`.
