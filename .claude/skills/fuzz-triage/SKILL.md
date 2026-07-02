---
name: fuzz-triage
description: Run the ZScheme differential fuzzer (zs-fuzz), triage the failures it finds, and write a detailed markdown bug report under issues/ for each genuinely new bug (skipping ones already documented there). Use when the user asks to run the fuzzer, hunt for compiler bugs, find new fuzzer failures, or refresh the issue backlog. This only documents bugs — it does not fix them (use /fix-issue for that).
argument-hint: "[iterations] [seed] (both optional)"
---

# Fuzz Triage

Run `zs-fuzz`, turn its raw failures into the same kind of detailed,
root-caused write-up already in `issues/` — see the existing files there for
the exact bar to hit — and skip anything already tracked. Read `docs/FUZZER.md`
first if you haven't recently; it explains the two-backend differential design,
the three oracles, and (critically, Section 7.2) the classes of bugs the
fuzzer *cannot* see, which matters when you're deciding whether a divergence
is real or a fuzzer/generator artifact.

This skill only produces issue files. Do not attempt to fix any bug you find
here — that's a separate step (`/fix-issue`), and mixing the two makes both
harder to review.

## 1. Run the fuzzer

```bash
dotnet run --project src/ZScheme.Fuzzer -- --seed <seed> -n <iterations>
```

- Use a fixed, explicit `--seed` (never the time-based default) so the run is
  reproducible and you can cite it in the report the way existing issues do
  (`seed 0x912140c6`).
- Default to `-n 1000` unless the user's argument says otherwise — that's what
  the existing issues were generated from, and it's enough volume to see
  clustering (many seeds hitting the same root cause) rather than one-offs.
- This can take a while. If it's likely to run past a couple of minutes, run
  it in the background rather than blocking, or drop `-n` for a quicker first
  pass and widen it later if the user wants more signal.
- Per CLAUDE.md: don't re-run the fuzzer repeatedly to grep for different
  things. One run, then inspect `cases.jsonl` / `session.json` /
  `artifacts/` as many times as you need from the saved output.

The run lands in `fuzz-runs/<UTC-stamp>-seed<hex>/`: `session.json` (summary),
`cases.jsonl` (one line per case), `artifacts/fuzz-failure-<seedhex>/` (full
repro dump per failure: `original.zs`, aux modules, `csharp-output.cs`,
`il-output.dll`, `report.json`).

If the exit code is `0`, there's nothing to triage — tell the user the run was
clean and stop.

## 2. Cluster the failures by root cause, not by surface message

Don't create one issue per failing seed — the existing issues each cover a
*family* of failures (one covers 85 of 93 failures in its run). Two failures
with different-looking error text can share a root cause (see the
`import-clr` issue: some seeds fail on both backends, others only on one,
purely due to timing non-determinism in the same underlying bug). Conversely,
don't assume superficially-similar messages share a cause without checking.

For each failure:
1. Read `report.json` to see which oracle failed (compile / ilverify /
   diffexec) and the raw error.
2. Open `original.zs` and, for compile/diffexec failures, the emitted
   `csharp-output.cs` / IL to see what was actually generated.
3. Trace the error back into the relevant compiler source (`TypeInferer.cs`,
   `CSharpEmitter.cs`, `IlEmitter.cs`, `ClrInterop.cs`, `ObjectLifter.cs`,
   etc. — whatever the symptom implicates) far enough to state an actual root
   cause, not just restate the symptom. This is real investigation, the same
   depth as reading a bug report someone else filed against your own code —
   grep for the failing construct's codegen/inference path and read it.
4. Group failures whose investigation converges on the same underlying cause.
   You don't need to fully investigate every single failure in a large
   cluster — once you've confirmed the pattern on a representative sample,
   it's fine to list the rest as "representative seeds" without repeating the
   full trace for each.

## 3. Check against existing issues before writing anything

Read every file already in `issues/`. For each cluster from step 2, check
whether its root cause matches an existing issue's `## Root cause` section
(same file/line, same underlying mechanism) even if the surface symptom or
seed differs. Also check whether it matches a *known, already-accepted* gap
called out in `docs/FUZZER.md` (e.g. the class-instance-call IL bug family
noted in §4.2/§6) that's already covered by an existing issue.

- **Already documented** → do not create a new file. If this run adds useful
  new evidence (a new representative seed, a wider blast radius, confirmation
  it's still present), you may mention that in your final summary to the
  user, but leave the existing issue file alone.
- **Genuinely new** → proceed to step 4.

When genuinely unsure whether two things share a root cause, say so in the
issue rather than guessing — a note like "worth checking whether this is the
same root cause as X" (as one existing issue does) is fine and honest.

## 4. Write one markdown file per new distinct bug

Match the structure and depth of the existing files in `issues/` exactly —
read a couple of them as templates before writing. Each new bug gets its own
file, kebab-case name describing the bug (e.g.
`csharp-emitter-field-as-method-call.md`), containing:

```markdown
# <Short, specific description of the bug as a title>

**Found by:** fuzzer run, seed `0x<hex>`, <N> iterations
(`fuzz-runs/<stamp>-seed<hex>/`)

**Affects:** <count> of <total> failures in this run — <breakdown by oracle/kind>.

**Representative seeds:** `<hex>`, `<hex>`, ...

Repro:
```
dotnet run --project src/ZScheme.Fuzzer -- --repro fuzz-runs/<stamp>-seed<hex>/artifacts/fuzz-failure-<hex>/original.zs
```

## Symptom

<the actual error text / observed divergence, verbatim where useful>

## Root cause

<what you found tracing through the compiler source, with file:line references>

## Suggested fix direction

<only if you have a concrete, confident direction — omit this section rather than guess>

## Priority note

<how this ranks against the other bugs found in *this* run — real correctness
bug vs. rejected-but-arguably-invalid program, blast radius, whether it's a
known/recurring family>
```

Before finalizing a report, actually run the repro command you're about to
cite and confirm it reproduces the failure — don't transcribe a command you
haven't verified.

## 5. Wrap up

Summarize for the user: how many failures, how many distinct root causes,
how many were already tracked vs. newly documented, and the filenames you
created under `issues/`. Do not commit these files yourself unless the user
asks — creating issue files is not the same authorization as "commit my
changes."
