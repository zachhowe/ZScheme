# Fuzzer generator expansion — status notes (2026-07-11)

Working notes from the generator coverage-gap expansion (plan:
variadic folds / symbols / non-exhaustive matches / shadowing / tuple arity /
collection breadth+conversions / when-unless-catch / Double subset / non-Int
OO members / macro depth / constraints). All three phases are **implemented,
smoke-tested, and validated**; this file records the validation evidence and
the follow-up work that remains.

## Validation summary

- **Old-generator baseline** (pre-change code, seed `12345`, 3000 cases):
  100% pass — 0 failures across compile / ilverify / diffexec.
- **New-generator run** (seed `99991`, 3000 cases): 2658 pass / 342 fail.
  **Zero compile-consistency (both-backends-fail) artifacts** — the hard
  generator-validity gate holds; every failure is a single-backend divergence,
  i.e. a compiler-bug candidate surfaced by the new coverage.
- Full test suite passes (2004 tests). A planned second new-generator batch at
  seed `12345` was cut short by request; nothing above depends on it.

## Failure breakdown of the seed-99991 run (342 total)

| Count | Class | Status |
|---|---|---|
| ~161 | CS0136/CS0128 — C# emitter fails on shadowed locals in nested contexts | documented: `csharp-local-shadowing-cs0136.md`; shadowing probe gated 0.25→0.10 |
| ~130 | CS1056 `$` (+ cascades) — `$cmp_N`/`$neq_N` chain fresh names emitted verbatim into C# | documented: `csharp-cmp-chain-dollar-names-invalid.md`; impure chain middles gated at 5% (still ~10%/program because several chains occur per program — could be tightened further if too noisy) |
| 21 | CS8121 — union value in tuple scrutinee not upcast by C# backend | documented: `csharp-tuple-union-scrutinee-not-upcast.md`; cross-ctor arm gated at 5% |
| 7 | `Compute() return diverged` (wrong values, e.g. IL=2147483647 vs CS=-95638) | **UNTRIAGED** — repros in `issues/repros/` |
| 7 | `Compute() outcome diverged (one threw, one returned)` | **UNTRIAGED** — repros in `issues/repros/` |
| ~16 | Misc Roslyn combos (CS0029/CS0103/CS0106/CS0116, CS0019...) | mostly cascades of the `$`-name bug; a residual distinct class may hide here — worth a second pass |

## Bugs found and documented this session (all still open)

1. `issues/il-string-equality-reference-compare.md` — IL lowers expression
   `=`/`!=` on String to `ceq` (reference equality); C# uses value equality.
   Minimal repro included. Match-pattern string literals are NOT affected
   (that path calls `String.Equals`).
2. `issues/csharp-tuple-union-scrutinee-not-upcast.md` — C# emits the concrete
   ctor type for union values inside `values` tuples; cross-ctor arms are
   CS8121. Minimal repro included.
3. `issues/csharp-cmp-chain-dollar-names-invalid.md` — comparison-chain
   desugar names (`$cmp_N`) are invalid C# identifiers. Minimal repro included.
4. `issues/csharp-local-shadowing-cs0136.md` — shadowed locals mis-scope in
   the C# emitter under nesting. Non-minimal repro preserved; trivial cases
   pass (minimization still to do).

## Untriaged: 14 diffexec divergences (highest-value follow-up)

Full failing programs preserved under `issues/repros/fuzz-failure-*.zs`
(named by case seed; re-run any with `zs-fuzz --repro <file>`). Suspicion
ranking, based on shapes present in the sources:

- The extreme values (`IL=2147483647` / `IL=-2147483551`) smell like
  overflow-adjacent shapes: variadic arith folds re-associating differently
  between backends, or `Math.Min/Max` / argmin/argmax reducers at INT_MIN/MAX.
- The throw-vs-return cases likely involve the new non-exhaustive-match
  fall-through probe (backends disagreeing on arm reachability/pruning) or
  div-by-zero reordering inside fold desugars.
- Some may reduce to the already-documented string-equality bug (equal
  contents reached indirectly).

Triage procedure: `/fuzz-triage` conventions — minimize each repro, dedupe
against the four issues above, write new issue files for anything novel.

## Gating levers (single sources of truth)

- `ProgramGenerator.Generate`: `EnableMatchFallthrough` 0.10,
  `EnableShadowing` 0.10 (raise to ~0.25 when CS0136 fixed),
  `EnableNullChecks` 0.08, unicode 0.20.
- `ExprGenerator.GenComparison`/`GenFloatComparison`: impure chain-middle
  probability 0.05 (lift when `$cmp` bug fixed).
- `MatchExprGenerator.GenTupleOfUnionMatch`: cross-ctor arm 0.05 (lift to ~0.2
  when CS8121 bug fixed).
- `SymbolExprGenerator.SymbolToStringEqToInt`: equal-content compare 0.10
  (lift when IL string-equality fixed).

## Deferred (needs oracle changes — out of scope per plan)

- TCO stack-depth probing: StackOverflowException is uncatchable and DiffExec
  runs `compute` in-process, so deep tail-recursion probes would kill the
  fuzzer host. Needs out-of-process or big-stack-thread execution.
- json/serialize stdlib coverage: parked.

## Docs

`docs/FUZZER.md` §4.2/§4.3/§4.4/§7.2 updated: new construct inventory, the
now-covered gaps removed (local shadowing), new language-level limits recorded
(Double ops, binary-only string-append, quoted lists, `class` constraint), and
the known-bug gates cross-referenced.
