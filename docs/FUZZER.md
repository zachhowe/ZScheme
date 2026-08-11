# The ZScheme Fuzzer

This document describes the ZScheme compiler fuzzer: what it does, how it is
built, and — just as importantly — the classes of bugs it is and is not able to
find. It is meant both as an operator's guide and as a design reference for
anyone extending the fuzzer.

The fuzzer lives in `src/ZScheme.Fuzzer/` (assembly `zs-fuzz`). It is a
standalone .NET console program, driven in CI and locally by
`run-fuzzer.ps1`. There is a stale duplicate under
`editor/zed/grammars/zscheme/...`; ignore it.

---

## 1. What the fuzzer is for

ZScheme compiles to .NET through **two independent backends**: a C# source
emitter (`Codegen/CSharpEmitter.cs`) and a direct IL emitter
(`Codegen/IlEmitter.cs`). Both consume the same front end (lexer → parser → AST
→ type inference → IR lowering) and diverge only at the final code-generation
stage.

That two-backend shape is the fuzzer's reason to exist. It generates random but
type-correct ZScheme programs, pushes each one through both backends, and checks
that the two backends **agree** — that they both compile it, that the IL is
verifiable, and that the compiled programs produce the same observable result.
Disagreement between the backends is, by construction, a compiler bug in (at
least) one of them.

This is **differential testing**, with the C# backend acting as the de-facto
reference for the IL backend. The strength of that design is that it needs no
hand-written specification or reference interpreter; its central limitation
(Section 7) is the flip side of the same coin: a bug shared by both backends is
invisible.

---

## 2. Architecture at a glance

```
                       ┌────────────────────────────────────────────┐
                       │  Program.cs  — driver / main loop           │
                       │  seed → per-case seeds → Parallel.For       │
                       └───────────────┬────────────────────────────┘
                                       │  one case
            ┌──────────────────────────▼───────────────────────────┐
            │  ProgramGenerator.Generate(caseSeed)                  │
            │   ~37 sub-generators emit ZScheme SOURCE TEXT         │
            │   → GeneratedProgram { mainSource, aux modules }      │
            └──────────────────────────┬───────────────────────────┘
                                       │
            ┌──────────────────────────▼───────────────────────────┐
            │  RunOracles  (short-circuiting pipeline)              │
            │   1. CompileConsistencyOracle  (both backends)        │
            │   2. IlVerifyOracle            (dotnet ilverify)      │
            │   3. DifferentialExecOracle    (run both, compare)    │
            └──────────────────────────┬───────────────────────────┘
                          pass │                 │ fail
                               ▼                 ▼
                       cleanup scratch    FailureArtifact dump
                                          + cases.jsonl entry
```

Key files:

| Concern | File |
|---|---|
| Driver / main loop | `Program.cs` |
| Options & defaults | `FuzzerOptions.cs` |
| Repro mode | `ReproRunner.cs` |
| Generation core | `Generation/ProgramGenerator.cs`, `Generation/ExprGenerator.cs`, `Generation/GeneratorContext.cs`, `Generation/Scope.cs` |
| Oracles | `Oracles/CompileConsistencyOracle.cs`, `Oracles/IlVerifyOracle.cs`, `Oracles/DifferentialExecOracle.cs` |
| Compiler-option wiring | `Oracles/CompilerOptionsFactory.cs` |
| Failure capture | `Reporting/FailureArtifact.cs` |
| Subprocess / runtime helpers | `Runtime/ProcessRunner.cs`, `Runtime/FuzzEnv.cs`, `Runtime/ReferenceAssemblyResolver.cs` |

---

## 3. The driver and the main loop

Entry is `Program.cs` (top-level statements). It supports two modes:

- **Fuzz mode** — `zs-fuzz [options]`: generate and test N random programs.
- **Repro mode** — `zs-fuzz --repro <file.zs> [--aux <dir>]`: re-run the oracles
  on a single, hand-editable `.zs` file. This is the manual-minimization tool
  (Section 6).

The fuzzing loop is built for **deterministic, parallelism-independent**
reproduction:

1. A master `Random` is seeded from `--seed`.
2. All per-case seeds are **pre-derived on the main thread** into a
   `caseSeeds[]` array *before* any parallel work starts. Because the case set
   is fixed up front, the programs generated for a given `--seed` are identical
   no matter how many workers run them.
3. Cases run under `Parallel.For` with `MaxDegreeOfParallelism = --workers`.
4. For each case: seed a fresh per-case `Random`, generate the program, write
   any aux modules into a per-case scratch `aux/` directory, then run the oracle
   pipeline.

Each per-case RNG is `new Random((int)(caseSeed ^ (caseSeed >> 32)))`, so the
same seed yields bit-for-bit identical programs.

Output goes to `fuzz-runs/<UTC-stamp>-seed<hex>/`:

- `session.json` — run summary (seed, counts, options).
- `cases.jsonl` — one line per case (seed, oracle result, optionally source).
- `scratch/` — per-case working directories (cleaned on pass).
- `artifacts/` — full failure dumps (Section 5).

The process exits `1` if any case failed, `0` otherwise — suitable for CI gating.

---

## 4. Generators

### 4.1 Approach: grammar/template-directed, string-based

Generators emit **ZScheme source text**, assembled with `StringBuilder` — not
AST nodes. This is deliberate: it exercises the *entire* pipeline including the
lexer and parser, and it makes failing cases human-readable `.zs` files you can
edit directly.

Generation is **loosely type-directed** but does **not** use the compiler's real
Hindley–Milner type system. Instead the generator keeps its own simplified
bookkeeping:

- A small `ExprType` enum — `Int, Bool, Float, String, IntFn, Long, Char, Byte`
  (`Generation/ExprType.cs`).
- A `Scope` (`Generation/Scope.cs`) tracking in-scope variable names by
  `ExprType`, so generated code only references variables that are actually
  bound and of the right shape.

`GeneratorContext` (`Generation/GeneratorContext.cs`) wraps the per-case
`Random` and provides the primitives every generator uses: `PickWeighted<T>`
(weighted random choice), `Fresh()` (fresh variable names `x0, x1, …`), and the
depth budget.

### 4.2 Program structure

`ProgramGenerator.Generate` wires ~37 sub-generators and emits, roughly in this
order:

- A `(namespace ZSchemeFuzzed)` and a `(module fuzz_<hex>)` header.
- 0–2 **aux modules** as separate `.zs` files, with a star-shaped import
  topology (and the occasional back-edge) to exercise the module resolver.
- A random subset of **21 stdlib modules** (option, result, vector, hash,
  string, math, core, cond, pipe, list, treelist, error, control, catch, and
  the concurrent/mutable collection families). Some gates force-add partners:
  `catch` pulls result+error+option (its expansion references `Err`/`Error`/
  `None` at the use site), `control` usually pulls mutable/vector (for an
  observable `when`/`unless` effect), and `list` pulls vector+treelist half the
  time so the cross-representation conversions can fire.
- `import-clr` bindings (CLR interop).
- 0–2 generic unions, 0–2 generic records, 0–2 non-generic structs.
- Occasional **macros** (a record-producing macro ~20%; expression macros ~30%:
  `when` / `let1` / `min2` plus a recursive **ellipsis** sum macro, a
  **literal-identifier** dispatch macro (`syntax-rules (plus minus)`), and a
  **hygiene-stress** macro whose template introduces an `x0` binding adjacent
  to user names — the expander is non-hygienic, so use sites are generated with
  `x0` retyped accordingly).
- 0–2 **interfaces** (method params/returns range over Int/Bool/Float,
  Int-biased); ~45% a **class** (standalone, interface-implementing, or an
  `#:open` base with a deriving override; standalone classes may carry one
  Bool/Float `#:mutable` field alongside the Int ones).
- 0–N **user functions**: regular, recursive (tail and non-tail), higher-order,
  or generic (`id`, `const`, `apply`, sometimes with `:where` constraints).
- Optional variadic helpers (~30%), `(delegate …)` helpers (~28%), and async
  helpers (~35%).
- The **entry point**: `(define (compute) : Int …)` or, in the async variant,
  `(define-async (compute) : (Task Int) …)`. This `compute` function is what the
  oracles invoke.

### 4.3 Expression generation

`ExprGenerator.cs` is the heart of generation. `GenInt`, `GenBool`, `GenFloat`,
and `GenString` each build a **weighted dispatch table**, gated on what is
actually available in the current context (which imports are present, which
variables are in scope, which CLR bindings and user types were emitted).

`GenInt` is the largest (~80 weighted reducers) and covers arithmetic and
div/mod (positive/negative-literal, `INT_MIN`/`-1` overflow, and runtime
div-by-zero / possibly-zero divisor shapes — all kept safe from Roslyn constant
folding), `if`, single-binding `let` / multi-binding sequential `let*` (later
bindings now deliberately reference earlier ones), recursive `letrec` groups,
lambda IIFEs,
`match`, user-function and generic calls, union matching, record access, every
stdlib collection reducer, `cond`/`pipe`, exception handling (`with`-handlers,
nested handlers, rethrow, many-handler forms), `use`/`use*` deterministic
disposal, string operations, `is-null?` checks (gated low), class
construct-and-call, CLR calls (`Math.Abs/Min/Max/Sqrt`, `String`
length/indexer, `Int32.TryParse`), `typeof`, delegate forms, wide-primitive
(`Long`/`Byte`) round-trips **and genuine `Long` arithmetic/equality via the
CLR `Int64` `Math` overloads + `BigMul`**, conversions, macro calls, and
aux-module calls. Separately, a `define-type-alias` declaration + an uncalled
helper that uses the alias in an annotation is emitted at the top level (~22%),
and string literals may include raw non-ASCII / surrogate-pair / control
characters (gated low).

Later additions to the expression surface:

- **Variadic operator folds** — n-ary `+ - *` (3–5 operands), n-ary `/` with
  literal divisors, unary `(- x)` / `(/ x)`, 3–4-operand comparison chains
  (`(< a (+ b c) d)` exercises the `$cmp_N` fresh-binding desugar; `(!= a b c)`
  expands all-pairwise), and 3–5-operand `and`/`or`; the same treatment at
  Float, where NaN-in-chain semantics are an extra probe.
- **`letrec` groups** (`LetrecExprGenerator`) — five shapes, one per lowering
  path: self-recursive, a mutually-recursive pair, a function capturing an
  enclosing local, a mixed value/function group (which forces the site's
  emission order — a value binding has to precede the closure that captures
  it), and a group left in scope as an `IntFn` so the shared reducers pass it
  around by value. The compiler lifts a group to top-level static functions
  with their captures prepended, and the two backends build the closure value
  differently (native lambda vs. synthesized display class), so a wrong capture
  set or a missed reference rewrite shows up as a backend disagreement.
  Termination is bounded *inside* each function (`n > 16 → (f 16)`), not at the
  call site as `UserFuncGenerator` does: a binding placed in scope as an
  `IntFn` can be applied to an arbitrary Int by `GenIntFnApply`, and an
  unbounded non-tail recursion would overflow the stack — which kills the
  fuzzer process outright instead of being reported as a case failure.
  Generated inside class/object declarations too, where fields are in bare-name
  scope (`GeneratorContext.InInstanceContext`): a group that reads instance state
  is hosted on the declaring class as a private method instead of being lifted to
  a static, so the capture shape draws from a scope including the fields. Only the
  value-position shape is dropped there — a private method has no delegate form,
  so leaving a member in value position is a compile error by design.
- **Nested `define`s** (`NestedDefineExprGenerator`, per-program
  `GeneratorContext.EnableNestedDefines`, rolled at 0.20) — a definition inside
  another function's body. `AstBuilder` desugars a run of adjacent body-level
  defines into a single `letrec` group whose body is the rest of the sequence, so
  this shares `LetrecLifter`'s lowering with the bullet above. What it probes
  that `letrec` cannot is the **desugar** itself: which forms land in which
  group, and what each group's body is. Both choices are invisible in the surface
  syntax and fail quietly — a group that scopes over one form too few, or two
  adjacent defines split apart, still compiles and only shows up as a wrong
  answer or a name-resolution failure. Seven shapes: self-recursive; a mutually
  recursive pair (which only works if the run is grouped); a define capturing an
  enclosing binding; a mixed value/function group; a group placed *after* an
  expression; two groups in one body where the second calls the first; and one
  left in scope as an `IntFn` so the shared reducers pass it around by value. The
  body a define needs comes from a randomly chosen wrapper — `begin`, a `let`
  body, or an immediately-invoked `lambda` — because all three reach
  `BuildExprSequence` from different callers. Termination is bounded inside each
  function for exactly the reason the `letrec` generator does it, and it is
  generated inside class/object declarations — minus the value-position shape —
  on the same terms too. This found a real
  capture bug: a nested group referencing an *enclosing* group's member has to
  inherit that member's captures, because the retargeted call must supply them.
- **Symbols** — `'lit` literals, `string->symbol` / `symbol->string`
  round-trips, symbol equality across construction paths (interning probe:
  each backend lowers symbol equality and symbol match-arms differently), and
  symbol-literal match arms.
- **Runtime-reached non-exhaustive matches** (per-case flag, ~10%) —
  literal-only int/float/string/symbol/tuple matches may omit the catchall
  (Warning-only at compile time) wrapped in `with-handlers`; both backends
  throw `InvalidOperationException("Non-exhaustive match")`, keeping the
  outcome oracle-comparable.
- **Binder shadowing** (per-case flag, ~25%) — let / let* / lambda-IIFE /
  match- and tuple-pattern binders occasionally rebind an in-scope name of the
  same type (including a later `let*` binding shadowing an earlier one while
  its RHS reads the shadowed value).
- **`values` tuples at arity 2–7** (weighted low, bumped at the 7 boundary —
  the `values` maximum and ValueTuple codegen edge).
- **Collection breadth**: list accessors (`car`/`cdr`/`rest`/`list-head`,
  incl. a wrapped `(car Nil)` throw probe), `reverse`/`append`/`concat`/
  `list-ref`/`map`/`filter`, the variadic `(list …)` ctor, and
  **cross-representation conversions** (list↔vector, list↔treelist,
  treelist↔vector); vector `make-vector`/`build-vector`/`vector-sort`/
  take/drop/count/filter-not/argmin/argmax/member (via `unwrap-or`) and the
  variadic `vector-append`.
- **`when`/`unless`** (Unit-typed bodies, observable via a mutable-vector
  write) and the **`catch`** macro reduced through an `Ok`/`Err` match.
- **Double (64-bit float) subset** — polymorphic `=`/`!=` at Double via
  `float->double`, and CLR `Math.Min/Max/Floor` Double overloads
  (`ExprType.Double` deliberately does not exist: no built-in ops are typeable
  at Double, so a Double scope var would be unusable).
- **Non-Int OO members** — interface methods over {Int, Bool, Float} params
  and returns, one optional Bool/Float `#:mutable` class field, with call
  sites reduced via the usual `ReduceToInt` idiom.
- **String concatenation in all three shapes** — deep hand-nested binary chains
  (4–6 leaves, left- and right-leaning), n-ary calls that `AstBuilder` left-folds
  into that same chain, and the 1-arg identity form — spelled both as
  `string-append` and as the string form of `+`; plus `contains?` and annotated
  `let` bindings `[x : Type v]` (~15%).

Four of these immediately surfaced compiler bugs, all now fixed.
Expression-level `=`/`!=` on String was reference equality on the IL backend
(bare `ceq`); the IL emitter now calls `String.Equals`, and the equal-content
probe in `SymbolExprGenerator.SymbolToStringEqToInt` is back to an even 0.5
split as a regression guard. The C# emitter's mis-scoping of shadowed locals
under nesting (CS0136/CS0128) is fixed — every local binder now goes through a
per-declaration-space uniquifier — so `EnableShadowing` runs at its intended
~25%. Comparison-chain fresh names (`$cmp_N`/`$neq_N`) reached the C# output
verbatim, where `$` is an invalid identifier character (CS1056); `NameConverter`
now folds `$` to `_` for both backends, so chain operands are generated
unrestricted again. And a union value inside a `values` tuple scrutinee kept its
concrete ctor type in the C# output, so any arm testing a sibling ctor was
rejected by Roslyn as impossible (CS8121); the emitter's scrutinee walk now
widens tuple elements to the union base as it already did for a direct union
scrutinee, and `MatchExprGenerator.GenTupleOfUnionMatch` generates the cross-ctor
arm at its intended ~20%. See
`issues/fuzzer-generator-expansion-2026-07-11-notes.md` for the validation
evidence, all gating levers, and the preserved-but-untriaged diffexec divergence
repros under `issues/repros/`.

Two invariants make the whole thing tractable for the oracle:

- **Everything bottoms out to `Int`.** Non-`Int` ground values are coerced back
  via `ReduceToInt` (bool → `(if e 1 0)`, float → `(float->int e)`), so the
  `compute : Int` contract always holds and the oracle has a single comparable
  result.
- **Depth is bounded.** Generation stops descending and emits a leaf once the
  depth budget hits zero. Integer leaves are biased toward `int.MinValue` /
  `int.MaxValue` (~10% each) to probe overflow behavior.

### 4.4 What is deliberately *not* generated (coverage gaps)

- **Mutual recursion between *top-level* defines is disabled.**
  `MutualRecFuncGenerator` exists but is unwired, because
  `TypeInferer.InferProgram` registers each define's type only after inferring
  its body, so a forward reference between siblings still fails to type-check.
  (The IL codegen half of that blocker is gone — the main-module emitter now
  registers every signature before emitting any body — so re-wiring it needs
  only an inference-side signature pre-pass.) Mutual recursion *is* generated
  through `letrec` and through adjacent nested `define`s, both of which pre-bind
  the whole group before any value is inferred.
- **Only `Int` (or `Task<Int>`) is ever the top-level result type.** Strings,
  chars, floats, and collections appear only internally and are reduced to
  `Int`. The oracle compares **ints only**.
- **Recursion always terminates.** The first argument of a recursive function is
  forced to a small literal in `0..20`, so the fuzzer checks the *value*
  produced by TCO, not non-termination or stack overflow from broken TCO.
  Active TCO stack-depth probing (deep tail-recursive loops that would
  stack-overflow if TCO broke) stays **deferred**: DiffExec runs `compute`
  in-process, and a StackOverflowException is uncatchable and would kill the
  fuzzer host — making it safe needs an out-of-process (or big-stack-thread)
  exec oracle change.
- No **top-level `define` shadowing chains** (local binder shadowing across
  let/let*/lambda/match sites *is* generated — see §4.3), no real I/O, no
  genuine concurrency (concurrent collections are exercised single-threaded),
  and no reflection beyond the fixed CLR bindings.
- It is **purely generative**: no seed corpus, no mutation, no coverage feedback.
- Aux modules suppress scope-dependent forms (e.g. `typeof`) via an
  `InAuxModule` flag.

Newly documented *language-level* limits (constructs the generator cannot emit):

- **`Long` `/`/`%` and `Long` ordered comparisons are ungeneratable.** The
  built-in `+ - * / < > <= >=` operators are constrained to `{Int,Float}`
  (`TypeEnv.cs`) and `%` is Int-only, so genuine 64-bit arithmetic is reached
  only through CLR `Int64` bindings (`Math.Abs/Min/Max`, `BigMul`) and 64-bit
  *equality* (via the polymorphic `=`); Long division/modulo and ordered
  comparison would need a compiler change to the numeric-kind set.
- **`define-type-alias` targets are CLR-type / `:array` only** — aliasing a
  primitive or tuple type is not supported by the language, so the alias
  generator only aliases open-generic / arity-0 CLR types and `:array`.
- **Nullable coverage is `is-null?`-only.** There is no nullable-*value* literal
  syntax; nullable *types* appear solely inside `typeof`.
- **`let` binds exactly one variable** (multiple bindings are `let*`, and a
  recursive binding group is `letrec`), so parallel multi-binding `let` is not
  a form the language has.
- **A group member cannot be left in *value* position inside a class or object
  declaration.** `letrec` and nested `define`s themselves *are* generated there —
  a group that reads instance state is hosted on the declaring class as a private
  method rather than lifted to a static — but a private method has no delegate
  form, so both group generators drop their value-position shape under
  `GeneratorContext.InInstanceContext` (`LetrecExprGenerator.Gen`,
  `NestedDefineExprGenerator.Gen`). Passing an instance-using group member around
  as a value is a compile error by design, not a divergence probe.
- **Generic nested `define`s are not generated.** Everything the generator emits
  bottoms out at `Int`, so a lifted function is never generic and the
  generic-lifting path — the lifted static declaring its own type parameters and
  the call site instantiating them explicitly — is covered only by
  `Integration/NestedDefineTests.cs` and `Integration/LetrecTests.cs`. The same
  gap makes the one remaining hard error unreachable here: a group member whose
  type mentions type variables used as a *value* rather than called.
- **A nested `define` in value position is generated, but only at `Int -> Int`**
  — the shared `Scope` models a function binding as the single pseudo-type
  `IntFn`, so a nested definition can only be passed around at arity 1.
- **Double arithmetic/ordered comparisons are ungeneratable** (same
  numeric-kind constraint as Long above): Double is reachable only via
  `float->double`/`double->float`, polymorphic `=`/`!=`, and the CLR Double
  `Math` bindings.
- **`-`, `*`, `/` and the ordered comparisons are numeric-only**; only `+` is
  typeable at String, so `(- "a" "b")` and `(< "a" "b")` are a both-fail.
- **Quoted lists are a parse error** (only symbols and self-evaluating literals
  can be quoted), and match has **no or-patterns / `:when` guards / `=>`**.
- The `class` where-constraint is not emitted (no reference-type ground exists
  to instantiate it with); `struct` / `unmanaged` / `default` / `notnull` /
  `new` all are.

---

## 5. Oracles

The three oracles are selectable with `--oracles compile,ilverify,diffexec`
(default: all three). They run as a **short-circuiting pipeline** in
`RunOracles`:

1. **Compile** always runs.
2. **IlVerify** runs only if both backends produced output.
3. **DiffExec** runs only after IlVerify passes.

Compiler options for both backends come from `CompilerOptionsFactory.cs`
(`Namespace = ZSchemeFuzzed`, `DisablePrelude = true`, stdlib package path
wired in).

### 5.1 CompileConsistencyOracle — "do both backends accept it?"

`Oracles/CompileConsistencyOracle.cs` compiles the same source through both
backends in-process (`OutputMode.CSharp` and `OutputMode.Il`) and compares
**compile success**:

- **PASS** only if both backends succeed.
- **FAIL** if exactly one backend succeeds (`"only one backend succeeded"`),
  if both fail, or if either backend throws an **uncaught exception** — the
  exception is surfaced rather than allowed to crash the fuzzer, and flagged as a
  likely internal compiler bug.

### 5.2 IlVerifyOracle — "is the emitted IL valid?"

`Oracles/IlVerifyOracle.cs` writes the IL `.dll` plus a runtimeconfig and shells
out to the `dotnet ilverify` tool (`Runtime/ProcessRunner.cs`, with reference
assemblies resolved by `Runtime/ReferenceAssemblyResolver.cs`). It **FAILs** on a
non-zero exit, a timeout, or any output line containing `[IL]:` / `Error:`.

This catches **unverifiable IL** the IL backend may emit — for example stack
imbalance. (There is a known class-instance-call IL bug; the generator gates that
path to ~30% of cases.)

### 5.3 DifferentialExecOracle — "do both backends compute the same thing?"

`Oracles/DifferentialExecOracle.cs` is the semantic oracle and the most
important one. It:

1. Roslyn-compiles the emitted **C# output** to a DLL in memory (a Roslyn
   failure here is itself a reported bug — `"Roslyn failed to compile C#
   output"`).
2. Loads both the IL DLL and the C# DLL into separate **collectible**
   `AssemblyLoadContext`s.
3. Reflectively invokes the static parameterless `Compute()` on the main
   module's class (the class name is reconstructed from the module name to mirror
   the compiler's `NameConverter`). For the async variant it blocks on
   `Task<int>` via `GetAwaiter().GetResult()` so a faulted task rethrows its inner
   exception unwrapped. Each invocation runs on a dedicated **background** thread
   bounded by `--timeout`; if either side does not finish in time the case
   **FAILs** with `"Compute() timed out (possible non-termination / broken TCO)"`
   instead of hanging the worker. The abandoned thread and its collectible
   `AssemblyLoadContext` leak (they can't be safely aborted mid-run), but
   `IsBackground` keeps the process able to exit; timeouts are rare because
   generation is constructed to terminate.

Then it compares outcomes:

| IL outcome | C# outcome | Verdict |
|---|---|---|
| returns `int` a | returns `int` b | **FAIL** if `a != b` (`"Compute() return diverged (IL=…, CS=…)"`), else PASS |
| throws | throws | **PASS** only if exception **type AND message** match (after unwrapping a single-inner `AggregateException`), else FAIL |
| throws | returns (or vice versa) | **FAIL** (`"one threw, one returned"`) |
| no `Compute` / non-int | — | **FAIL** (`"Compute() invocation errored"`), distinct from a program runtime error |

So the model is: **C#-backend-as-reference vs IL-backend**, differential on a
single `Int` return value (or thrown-exception identity).

---

## 6. Shrinking / minimization

**There is no automatic shrinker.** Minimization is **manual**, but well
supported:

- On failure, `Reporting/FailureArtifact.cs` writes a complete artifact directory
  named `fuzz-failure-<caseSeedHex>` containing `original.zs`, any aux module
  sources, the emitted `csharp-output.cs`, the `il-output.dll` (plus
  runtimeconfig), scratch copies, and a `report.json`.
- Aux modules are saved under their **declared module name** (`aux_<hex>_0.zs`),
  flat beside `original.zs`. That name is load-bearing: the `ModuleResolver`
  finds an import by looking for `<module-name>.zs` on a search path, so a
  decorated file name would make the artifact unreplayable.
- `ReproRunner.cs` (`zs-fuzz --repro file.zs`) re-runs the compile and diffexec
  oracles against a single `.zs` file, defaulting the module search path to the
  repro's **own directory** — so a saved artifact (or a repro copied whole out of
  one) replays with no extra flags. `--aux <dir>` overrides that when a
  hand-reduced repro keeps its modules elsewhere. The intended workflow is:
  reduce `original.zs` by hand, re-run `--repro`, and confirm the divergence
  still fires. When the consistency oracle is the failing one, `ReproRunner` also
  runs the IL assembly directly so the divergence is inspectable.
- **When copying a repro out of a run directory, take the aux modules with it.**
  A multi-module `original.zs` moved on its own fails with
  `Module not found: 'aux_<hex>_0'` on *both* backends — which looks like a
  compile-consistency failure but is just a missing file.

---

## 7. What the fuzzer can and cannot detect

### 7.1 Failures it CAN detect

- **Compile divergence** between the backends — one backend rejects what the
  other accepts.
- **Compiler crashes** — uncaught exceptions anywhere in either backend
  pipeline.
- **Invalid / unverifiable IL** — verifier errors and stack imbalance, via
  ilverify.
- **Miscompilation observable as divergence** — different `Int` results,
  different thrown-exception type/message, or one-throws-one-returns between the
  backends.
- **C# output Roslyn refuses to compile** — a malformed-emission bug in the C#
  backend.
- **Overflow, div-by-zero, index-out-of-range** edge cases — but only insofar as
  the two backends *disagree* about them.
- **Hangs/timeouts** in ilverify (bounded by `--timeout`).

### 7.2 Failures it CANNOT detect

The crucial caveat: the C# backend is the *de facto* oracle, so the fuzzer can
only see bugs where the two backends **disagree**.

- **Shared miscompilations.** If both backends compute the *same wrong* answer,
  DiffExec passes. There is no independent reference interpreter or spec oracle,
  so any bug in a **shared upstream stage** — lexer, parser, AST builder, type
  inferer, IR lowering — or a bug duplicated in both emitters is invisible to the
  differential check.
- **Non-`Int` observable behavior.** Only `Compute()`'s single `Int` return (or
  exception identity) is compared. Side effects, printed output, mutation
  visibility, ordering, and string/float/collection *contents* are not observed
  unless they fold into the final `Int`.
- **Type-soundness violations** that still produce matching ints. There is no
  dedicated type-soundness oracle; if the checker wrongly accepts a program but
  codegen happens to agree, nothing fires.
- **Parser/printer round-tripping.** There is no source-reconstruction oracle.
- **Non-termination / stack overflow from broken TCO.** Recursion is bounded to
  terminate by construction, so only TCO *value* correctness is checked, not the
  optimization's effect on stack depth. (DiffExec now *bounds* each `Compute()`
  invocation with `--timeout` and reports a timeout rather than hanging — see
  §5.3 — but generation still terminates by construction, so this is a safety
  net, not active stack-depth probing.)
- **Concurrency / race bugs.** Concurrent collections are used single-threaded;
  the only parallelism is across independent cases.
- **Anything in disabled paths** — e.g. mutual recursion.
- **Deliberately-probed known/suspected divergences.** `is-null?` is generated at
  a very low per-program rate (like the string-indexer path): it is *confirmed* to
  surface an IL-backend bug — `is-null?` lowers to `ReferenceEquals(x, null)` and
  the IL backend leaves a value-type operand unboxed (ilverify `StackUnexpected:
  found Int32, expected ref 'object'`) while the C# backend boxes it. Raw
  non-ASCII / surrogate-pair / control-char string literals probe the
  source-encoding path (oracle-clean in practice). These surface bugs by design;
  when they do the case is triaged/documented, not fixed here.

---

## 8. Reproducibility

- `--seed <long>` sets the master RNG seed; the default is time-based
  (`DateTime.UtcNow.Ticks & 0x7FFF…`). Both decimal and `0x`-prefixed hex forms
  are accepted, so the hex seed a session dir / `caseSeedHex` reports can be
  passed back verbatim.
- Per-case seeds are pre-derived from the master seed and case index, so worker
  count does **not** affect the case set.
- The session directory encodes the seed (`fuzz-runs/<stamp>-seed<hex>/`), every
  case logs its `caseSeed`/`caseSeedHex` in `cases.jsonl`, and failure artifacts
  are named `fuzz-failure-<caseSeedHex>`.

**Caveat:** there is no "run only case seed X" flag — a per-case seed is derived
from `master ⊕ index`, so to reproduce one specific case you either replay the
whole session with the same `--seed` **and** the same `--max-depth` /
`--max-funcs` (those parameters change generation), or — more practically — feed
the saved `original.zs` straight back through `zs-fuzz --repro`.

---

## 9. Configuration reference

All from `FuzzerOptions.cs`:

| Flag | Default | Meaning |
|---|---|---|
| `--seed <long>` | time-based | Master RNG seed (decimal or `0x`-hex) |
| `--iterations <n>`, `-n` | 1000 | Number of cases |
| `--max-depth <n>` | 5 | Max expression-tree depth (floored to ≥1) |
| `--max-funcs <n>` | 3 | Max user functions per program |
| `--oracles <list>` | all three | `compile,ilverify,diffexec` |
| `--output-dir <path>` | `<repo>/fuzz-runs` | Base output dir |
| `--repo-root <path>` | auto-discovered | Overrides the walk-up search for `ZScheme.slnx` |
| `--keep-passing` | off | Save passing-case source in `cases.jsonl` |
| `--timeout <secs>` | 10 | Per-subprocess timeout (ilverify) **and** per-`Compute()` execution bound (DiffExec, one budget per backend invocation) |
| `--workers <n>`, `-j` | `ProcessorCount` | Parallel workers |
| `--verbose`, `-v` | off | Log each case |

Additional hardcoded internal limits in `ProgramGenerator`: aux-module body
depth capped at `min(maxDepth, 3)`; recursive-function body depth capped at 3;
recursive first-argument literal in `0..20`; per-program type/function counts
(unions/records/structs/interfaces 0–2, async funcs 1–3, etc.). `letrec` and
nested-`define` bodies clamp their own recursion at `RecursionBound = 16` and
their body depth at `min(depth - 1, 2)`.

---

## 10. Running it

```bash
# Via the driver script (restores the ilverify tool, builds, runs):
pwsh ./run-fuzzer.ps1

# Directly, with a fixed seed for reproducibility:
dotnet run --project src/ZScheme.Fuzzer -- --seed 12345 -n 5000

# Run a single saved failing case (aux modules in the same dir are found automatically):
dotnet run --project src/ZScheme.Fuzzer -- --repro fuzz-runs/<run>/artifacts/fuzz-failure-<hex>/original.zs
```

A non-zero exit code means at least one case failed; inspect the
`artifacts/fuzz-failure-*` directories and `cases.jsonl` in the run directory.

---

## 11. Ideas for extension

The gaps in Section 7.2 map directly to potential improvements:

- An **independent reference oracle** (a tree-walking interpreter over the typed
  AST or IR) would break the "shared bug" blind spot — it would let the fuzzer
  catch miscompilations common to both backends and bugs in shared front-end
  stages.
- **Richer result observation** — compare non-`Int` return types, captured
  stdout, or a serialized state — would widen semantic coverage beyond a single
  int.
- **Automatic shrinking** (delta-debugging over the S-expression structure) would
  remove the main manual step in triage.
- **Active TCO stack-depth probing.** The DiffExec execution timeout (background
  watchdog thread, §5.3) is now in place, so the recursion-always-terminates
  constraint could be relaxed to deliberately generate deep/unbounded recursion
  and check that broken TCO is caught as a timeout/stack-overflow divergence
  rather than a hang.
