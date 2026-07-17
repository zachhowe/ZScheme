# TCO loop-body emitters duplicate the existing statement/match rendering

**Type:** tech debt / refactor (not a bug). No incorrect behavior — the TCO path is
covered by `EndToEndTests` (both backends, `if`/`let`/`match`/`begin`/union-match/arg-swap)
and 3000-program differential fuzzing (compile / ilverify / diffexec, 0 failures). This is a
DRY / maintainability smell only.

**Found by:** self-review after wiring `TailCallLowering` in and making both emitters render
TCO loops mechanically (the "emitters as dumb as possible" work). The tail-call *intelligence*
is fully centralized in `TailCallLowering`; what remains is that each backend grew a **second**
statement-body/match renderer for the loop path that overlaps the one it already had.

## What / where

To emit an `IsTcoLoop` function's body, each backend walks the `if`/`let`/`match`/`begin`
spine placing `return`/`continue` (C#) or `Ret`/`Br` (IL) at the leaves. That walk re-implements
logic the backend already owns for ordinary bodies:

C# backend (`src/ZScheme.Compiler/Codegen/CSharpEmitter.Emit.cs`):
- `EmitTcoLoopBody` (`:441`) overlaps `EmitStatementsBody` (`:1929`) — both walk `If`/`Let`
  placing leaf statements. `EmitTcoLoopBody` additionally handles `TcoJump → continue`, `Seq`,
  `Match`, and (critically) emits the `return;` that a `Unit`-returning loop needs to terminate
  the `while(true)`.
- `EmitTcoLoopMatch` (`:520`) re-implements `EmitMatch` (`:1417`) as a `switch` **statement**
  instead of a `switch` **expression** — duplicating `PruneUnreachableArms`, the ZSymbol-guard
  special case, `IsIrrefutableForType`/`ArmsAreExhaustive` fallback logic, and the
  `PushLocal`/`PopLocals` binding dance.

IL backend (`src/ZScheme.Compiler/Codegen/IlEmitter.Emit.cs`):
- `EmitLoopBody` (`:875`) mirrors `EmitIf` (`:1354`) and `EmitLet` (`:1382`).
- `EmitLoopMatch` (`:998`) mirrors `EmitMatch` (`:2236`) — same scrutinee-store + `EmitPatternTest`
  arm dispatch, minus the shared end-label / `ReconcileBranchStack` / `Br end`.

## Root cause — why the duplication exists

The loop body cannot reuse the existing renderers as-is:

1. **C# `EmitStatementsBody` has the wrong leaf terminator for a loop.** Its `default`/`Unit`
   arm emits the value *without* a trailing `return;` (`:1929`+). In a `while(true)` a
   `Unit`-typed base case must emit `return;` or control falls through and loops forever. It
   also has no `Match`, `Seq`, or `TcoJump` case.
2. **The C# match is a `switch` expression.** A `TcoJump` arm needs `continue;`, which is a
   statement and cannot live in a `switch` expression arm — hence a whole separate
   `switch`-statement renderer (`EmitTcoLoopMatch`).
3. **The IL backend has no statement-body emitter at all.** `EmitNode` is a pure
   expression model that leaves exactly one value on the stack, with a single trailing `Ret`
   in `EmitFuncBody` (`:747`). A loop needs per-leaf `Ret`/`Br`, so `EmitLoopBody`/`EmitLoopMatch`
   are genuinely new control-flow renderers with no existing counterpart to extend.

So the tail-position *spine shape* (recurse into `If` branches, `Let`/`Match`/`Seq` tails) is now
encoded in **three** places: `TailCallLowering.RewriteTail` plus each backend's loop walker.
That triplication is the smell — it means a future change to how a spine renders (a new
tail-position-bearing node, a new pattern kind, a match-lowering tweak) must be made in the
loop renderer *and* the ordinary renderer or they silently drift.

## Suggested fix direction

Unify loop and non-loop statement rendering per backend so there is one walker that also knows
`TcoJump`:

- **C#:** extend `EmitStatementsBody` to (a) take a "terminator" strategy so a leaf emits
  `return`/`continue`/`break` uniformly (fixing the `Unit`-return case for both callers), and
  (b) handle `Seq`, `Match` (statement form), and `TcoJump`. Then `EmitTailRecursiveLoop` calls
  it instead of a bespoke `EmitTcoLoopBody`. Factor the match-arm pattern/prune/exhaustiveness
  logic shared by `EmitMatch` and the statement-form match into one helper so the `switch`
  expression and `switch` statement forms differ only in how an arm *body* is rendered.
- **IL:** introduce a single statement-oriented body walker parameterized by a leaf action
  (`Ret` vs `Br`-to-loop-start vs `Br`-to-shared-end) so `EmitFuncBody`'s normal path, and the
  loop path, and match-arm rendering all share it. This also removes the `EmitLoopMatch` /
  `EmitMatch` fork.

The goal: the tail-position spine shape lives in exactly one renderer per backend, and
`TcoJump` is the only loop-specific node it special-cases.

## Priority note

Low. Nothing is wrong at runtime and the paths are well tested. Worth doing before the next
change that touches statement-body or match lowering, to avoid the two copies drifting. Pure
internal refactor — no language-visible behavior should change, so the existing TCO
`EndToEndTests` plus a fuzzer run are sufficient regression coverage.
