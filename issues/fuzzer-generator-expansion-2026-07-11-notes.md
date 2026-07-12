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
| ~161 | CS0136/CS0128 — C# emitter fails on shadowed locals in nested contexts | **FIXED** — every local binder now routes through a per-declaration-space uniquifier in the C# emitter; issue file deleted, shadowing probe restored to 0.25 |
| ~130 | CS1056 `$` (+ cascades) — `$cmp_N`/`$neq_N` chain fresh names emitted verbatim into C# | **FIXED** — `NameConverter.ReplaceSpecialChars` now folds `$` to `_`, so both backends emit `_cmp_N`; issue file deleted, chain operands generated unrestricted again |
| 21 | CS8121 — union value in tuple scrutinee not upcast by C# backend | **FIXED** — the C# emitter's scrutinee walk now recurses into `values` tuples and widens union elements to the union base; issue file deleted, cross-ctor arm restored to 0.2 |
| 7 | `Compute() return diverged` (wrong values, e.g. IL=2147483647 vs CS=-95638) | **UNTRIAGED** — repros in `issues/repros/` |
| 7 | `Compute() outcome diverged (one threw, one returned)` | **UNTRIAGED** — repros in `issues/repros/` |
| ~16 | Misc Roslyn combos (CS0029/CS0103/CS0106/CS0116, CS0019...) | confirmed mostly cascades of the `$`-name bug. Roslyn resyncs badly after the `$` parse error and emits nonsense CS0128s (`'var' is already defined`) — do **not** mistake these for shadowing failures. One genuine residual class does hide here: **CS0029 on object-expression methods** — an `(object ...)` literal implementing a `Bool`-returning interface method emits `public bool M(...) { return this.P0; }` against an `Int` field. Undocumented; needs its own issue |

## Bugs found and documented this session

1. ~~IL lowers expression `=`/`!=` on String to `ceq` (reference equality);
   C# uses value equality.~~ **FIXED** — `IlEmitter.EmitBinaryOp` now calls
   `String.Equals(string, string)` for String operands on both `=` and `!=`,
   mirroring the pattern path. Issue file deleted; regression test
   `EndToEndTests.StringEquality_ComputedOperand_ComparesByValueIl`.
2. ~~`issues/csharp-tuple-union-scrutinee-not-upcast.md` — C# emits the concrete
   ctor type for union values inside `values` tuples; cross-ctor arms are
   CS8121.~~ **FIXED** — the emitter already widened a *direct* union scrutinee
   to its base type; that walk now recurses through tuple scrutinees, pairing
   each element with the patterns that land in its position so the cast is only
   inserted where an arm actually tests a case. Issue file deleted; regression
   tests `CSharpEmitterTests.EmitMatch_UnionValueInTupleScrutinee_
   UpcastsToUnionBase` and `EmitMatch_TupleScrutineeWithoutCasePatterns_
   KeepsElementsUncast`. A 400-case replay of the seed-99991 corpus with the
   cross-ctor arm back at 0.2 produces zero failures of any class.
3. ~~`issues/csharp-cmp-chain-dollar-names-invalid.md` — comparison-chain
   desugar names (`$cmp_N`) are invalid C# identifiers.~~ **FIXED** — the names
   *were* routed through the sanitizer (contra the write-up); `NameConverter`
   simply had no case for `$`, so it survived into the C# output. It now folds
   to `_`. Issue file deleted; regression tests
   `CSharpEmitterTests.EmitComparisonChain_ImpureMiddleOperand_
   EmitsValidCSharpIdentifier` and `EmitNeqAllDistinct_ImpureOperands_
   EmitsValidCSharpIdentifiers`. A 400-case run with chain operands unrestricted
   produced zero CS1056s (the 9 residual failures are all CS8121, i.e. the
   still-open tuple-union bug).
4. ~~`issues/csharp-local-shadowing-cs0136.md` — shadowed locals mis-scope in
   the C# emitter under nesting.~~ **FIXED** — minimised to a `let` shadowed
   inside `if` branches; only the top-level let *spine* was guarded before, so
   any shadow one level down slipped through. All local binders (let / use /
   lambda params / catch vars / match pattern vars) now go through a
   per-declaration-space uniquifier. Issue file and its repro deleted;
   regression tests `CSharpEmitterTests.EmitLet_ShadowingInsideIfBranches_
   RenamesToAvoidCs0136` and `EmitLet_ShadowingInsideLambdaBody_KeepsPlainName`.

## Untriaged: 14 diffexec divergences — RESOLVED/REVISED

Re-run of every preserved repro against the **fixed** IL string-equality
emitter (see below) settles this pile:

- **6 of the 14 are closed by the string-equality fix** and their repro files
  have been deleted (`1982a0a0`, `1d7df8a7`, `ab207bdf`, `b8e3298d`,
  `bc152975`, `d6947eea` — all now `All oracles passed`). Independently, a
  400-case replay of the seed-99991 corpus goes from 9 value/outcome
  divergences to **zero** with the fix in.
- The **overflow suspicion in the original ranking was wrong.** The extreme
  values (`IL=2147483647`, `IL=-2147483648`) were not re-association or
  INT_MIN/MAX reducer bugs — they were just *whichever branch the miscompiled
  string comparison selected*. The divergent value is the branch body's
  arithmetic; the root cause was the branch condition. Same for at least some
  of the throw-vs-return cases (a wrongly-taken branch reaching a throwing
  expression), not the match-fall-through probe as guessed.
- The remaining **8 repros are not replayable standalone**: they are
  multi-module programs and the generated `aux_*` module files were never
  preserved alongside the main `.zs`, so `--repro` dies with
  `Module not found: 'aux_<seed>_0'` on *both* backends. This is an
  **artifact-preservation gap in the fuzzer**, not a compiler bug, and it
  means these 8 divergences are currently unreproducible. Follow-up: make the
  artifact writer emit the full module set (or inline aux modules into the
  saved repro), then re-run — some may well be dead too, since they came from
  the same corpus.

## Gating levers (single sources of truth)

- `ProgramGenerator.Generate`: `EnableMatchFallthrough` 0.10,
  `EnableShadowing` **0.25** (restored — the CS0136 bug it was gated for is fixed),
  `EnableNullChecks` 0.08, unicode 0.20.
- `ExprGenerator.GenComparison`/`GenFloatComparison`: chain operands
  **unrestricted** (restored — the `$cmp_N` bug they were gated for is fixed).
- `MatchExprGenerator.GenTupleOfUnionMatch`: cross-ctor arm **0.2** (restored —
  the CS8121 bug it was gated for is fixed; the shape is its regression guard).
- `SymbolExprGenerator.SymbolToStringEqToInt`: equal-content compare **0.5**
  (lifted from 0.10 now that the IL string-equality bug is fixed; the shape is
  the regression guard for it).

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
