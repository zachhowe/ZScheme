---
name: zs-code-review
description: Review ZScheme changes for repository rules, general .NET code quality, and how Scheme-like compiler changes are. Use when asked to review a diff, working-tree changes, a commit range, or a PR in this repository; do not use to implement fixes or write new code.
---

# Review ZScheme changes

Reviewing is read-only. Reading the diff requires git read commands (`status`,
`diff`, `log`); the review request is the authorization. Never stage, commit,
push, or edit code unless asked afterwards.

## 1. Scope the change

Identify what to review: the working tree (`git status`, `git diff HEAD` plus
untracked files) by default, or the commit range / branch / files the user
named. Read enough of each touched file to judge the change in context — not
just the hunks. Skim `issues/*.md` so you recognize a known bug family if the
change re-introduces one.

## 2. Build context before judging

Read the documentation for the areas touched:

| Area | Read |
| --- | --- |
| Pipeline stages (lexing, parsing, macros, AST, inference, lowering, codegen) | `docs/COMPILER-PIPELINE.md`, relevant stage section |
| New or changed syntax / special forms | `docs/SYNTAX-FORMS.md` |
| CLR interop and type mapping | `docs/CLR-TYPE-MAPPING.md` |
| zsup / toolchain | `docs/TOOLCHAIN.md` |
| Fuzzer | `docs/FUZZER.md` |
| Tests using mocks | `docs/MOCKS.md` |

Also read two or three neighboring implementations of the same kind of code
(sibling AST visitor, sibling lowering case) and match the established local
idiom.

## 3. Repository rules (from AGENTS.md — violations are should-fix at minimum)

- AST, IR, types, S-expr, and token nodes are **sealed records**; every node
  carries a `SourceSpan`. New node kinds and desugarings must thread spans from
  source so diagnostics point at user code.
- Node dispatch uses **switch expressions with type patterns**, not if/else
  chains on node types.
- User errors go to **`DiagnosticBag`** (`Error`/`Warning` with a span and, for
  new diagnostics, a `ZSnnnn` code consistent with the existing series). Stages
  never throw for user errors and keep going where possible; short-circuiting
  happens via `HasErrors` between stages.
- **Mocks** follow `docs/MOCKS.md`: no logic — only call recording, configurable
  results, events, `ClearTracking()`.
- **Docs move with behavior**: pipeline changes update `COMPILER-PIPELINE.md`,
  zsup changes `TOOLCHAIN.md`, fuzzer changes `FUZZER.md`. A change that alters
  documented behavior without the doc change is a finding.
- Version bumps only via `bump-version.ps1`; flag manual version edits.
- Formatting happens at commit time: do not run or request
  `format-all-cs.ps1`, and do not flag whitespace/StyleCop-style diffs it would
  normalize.

## 4. Scheme-likeness of compiler changes

This is the review's distinctive lens. ZScheme is immutable-by-default,
expression-oriented, pattern-matched, TCO'd, and errors-as-values. Check that
compiler changes preserve those properties rather than accreting .NET-isms:

- **Desugar before adding.** New syntax should expand into the existing core
  spine (`if`/`let`/`letrec`/`match`/`begin`) in the AST builder, so inference,
  the analyzers, and both backends need no new cases — the precedent for
  `cond`/`when`/`begin`. A genuinely new special form must answer: how does it
  participate in `match`, in tail position, and in statement-vs-expression
  lowering (follow the `use`/`with-handlers` precedent)?
- **Tail calls survive.** New constructs must not silently break tail position.
  Anything that adds a frame barrier (`try`, `using`, `await` structure) must be
  reflected in `TailRecursionAnalyzer` in sync with `TailCallLowering` — that
  contract is pinned by `TailRecursionDriftTests`; a change that desynchronizes
  them is a blocker. A new barrier must *warn* (like the `barrier` reason of
  ZS0005), never silently lose TCO.
- **Immutable by default.** Language features must not introduce implicit
  mutability; mutability stays opt-in and visibly spelled (`Mutable-*` types).
  In host code, mirror it: `readonly`, immutable collections, no shared mutable
  state between compiler passes.
- **Everything is an expression.** Every form yields a value (`Unit` for
  effects). Statement-only forms are a smell; the statement/expression split
  belongs in lowering, not in the language.
- **Errors are values or diagnostics.** User-facing failure is `Result`/`Option`
  in stdlib and `DiagnosticBag` in the compiler. C# exceptions are for internal
  invariant violations only; a new `throw` reachable from bad user input is a
  finding.
- **Pattern matching is first-class.** New data shapes (unions, records) support
  `match` with exhaustiveness (missing cases flow into ZS0002's structured data);
  the compiler's own dispatch mirrors the language it implements.
- **Inference stays honest.** New forms unify and generalize like everything
  else; no annotation-only features inference cannot see, no dynamic escape
  hatches beyond the sanctioned CLR interop boundary. Preserve forward-reference
  semantics (declared signature, not generalized).
- **Backend parity.** Lowering/codegen changes must behave the same on the C#
  and IL backends or fail explicitly; flag IL-only or C#-only paths, and changes
  tested against only one backend (TCO, `use`, `with-handlers` are the
  precedents for testing both).
- **The surface stays Scheme.** Hyphenated identifiers, `?` predicate suffix,
  Racket-flavored names, unshadowable special forms, `syntax-rules` macros.
  New diagnostics are actionable, point at source, and offer escape hatches
  consistent with precedent (`#:recursive` markers, `--no-warn-*` flags).
- **Source round-trips.** The lexer/parser are lossy (see the note in
  `COMPILER-PIPELINE.md`); anything re-emitting source from tokens/S-exprs must
  compensate — check quoted strings, spans, and quote sugar.

## 5. General .NET review

- **Nullability**: NRT annotations accurate; `!` only with a real invariant;
  external input (paths, manifests, package metadata) guarded.
- **Types and members**: file-scoped namespaces; `sealed` by default; records
  for values; `_camelCase` fields; collection expressions (`[]`); no public
  mutable collections — expose `IReadOnlyList<T>`/`IReadOnlyDictionary<K,V>`.
- **Exceptions**: no swallowed catches; no bare `catch` without rethrow,
  logging, or a diagnostic; no control flow by exception in pipeline stages.
- **Async**: no `.Result`/`.Wait()`; propagate `CancellationToken`; async only
  where there is real I/O.
- **Disposal**: `using`/`IAsyncDisposable` for streams, processes, temp files;
  ownership clear on every `IDisposable` field.
- **Hot paths** (lexer, parser, inference, codegen): flag per-node LINQ/alloc
  only where the code is demonstrably hot; otherwise prefer clarity over
  speculative optimization.
- **Robustness**: validate inputs that come from outside the compiler
  (manifests, package paths, imported sources) — skim `issues/` for the flavor
  of what slips through.
- **Tests**: new behavior is covered by the narrowest sensible test; tests
  assert behavior, not implementation; mocks obey `MOCKS.md`.

## 6. Verify proportionately

Run the narrowest relevant checks, not everything by default:

```powershell
dotnet build
dotnet test --filter "FullyQualifiedName~TouchedTestName"
```

For compiler changes, add `pwsh ./run-package-tests.ps1`,
`pwsh ./run-package-csharp-tests.ps1`, and `pwsh ./build-examples.ps1` when the
change could affect packages or examples. Per AGENTS.md, save one run's output
to a temporary file and inspect that file instead of re-running to search
output. Report verification results as part of the review, including failures
that pre-date the change (verify by checking whether the touched code could
cause them).

## 7. Report

Lead with a short summary of what the change does and your overall assessment.
Then findings by severity:

- **Blocker** — correctness bugs, broken invariants (span loss, TCO drift,
  backend divergence, exceptions on user input), docs contradicting behavior.
- **Should fix** — repository rule violations, missing doc updates, missing
  tests, API surface or nullability problems.
- **Nit** — naming, local idiom, minor clarity.

Each finding: `path:line`, what is wrong, why it matters, and a concrete
suggestion (a sketch, not a rewritten file). Close with what you checked and
verified. Do not apply fixes or commit; offer to implement the agreed findings
afterwards if the user wants.
