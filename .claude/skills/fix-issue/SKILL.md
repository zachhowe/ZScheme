---
name: fix-issue
description: Work through the bug reports in issues/ — pick one, fix it, verify it, delete the issue file, and commit. Use whenever the user asks to clear the issue backlog, fix a documented/fuzzer-found bug, "work on an issue", or asks what's next in issues/. Also use if they name a specific issue file or keyword to target.
argument-hint: [issue filename or keyword, optional]
---

# Fix Issue

Turn one file in `issues/` into a merged fix. Each file there is a standalone
bug report (often fuzzer-found — see `docs/FUZZER.md`) with a repro command,
a symptom, usually a root-cause analysis, and a "Priority note" ranking its
severity. Treat that analysis as a strong lead, not gospel — verify it against
the actual code before trusting it.

## 1. Survey and pick

Read every `*.md` file in `issues/`. If the user named a specific file or
keyword as an argument, use that one. Otherwise pick using this order:

1. Prefer issues whose "Priority note" explicitly says it's higher priority
   than the others (e.g. "highest priority of the three findings").
2. Among remaining issues, prefer real correctness bugs (wrong output/crash)
   over rejected-but-arguably-invalid programs, and prefer higher affected
   counts over one-off occurrences.
3. If issues reference each other (e.g. one says "worth checking whether this
   is the same root cause as X"), read both before deciding — you may be able
   to close two with one fix, or discover they're unrelated.

State which issue you picked and why in one sentence before moving on.

## 2. Understand and reproduce first

Before writing any fix:

- Read every source file the issue points at (root cause section, line
  references).
- Actually run the repro command from the issue (`dotnet run --project
  src/ZScheme.Fuzzer -- --repro <path>`, or whatever it specifies) and confirm
  you see the same failure. Don't skip this — issue write-ups can be stale or
  slightly wrong about the exact failure mode, and you need the real error
  text to know when it's fixed.
- If the root-cause section's hypothesis doesn't match what you observe,
  trust your own investigation over the write-up.

## 3. Fix it

Make the minimal, targeted change that addresses the root cause — not a
workaround that just suppresses the symptom. Follow the conventions in
CLAUDE.md (sealed records, DiagnosticBag for errors, SourceSpan on new nodes,
switch-expression dispatch, stdlib collection ops via `import-clr :instance`,
etc.) and match the style of the surrounding code.

If the bug is backend-specific (C# emitter vs IL emitter), don't touch the
other backend unless the issue says both are affected.

## 4. Verify

Don't run the same build/test script repeatedly with different filters —
save output to a temp file first (per CLAUDE.md) and grep that.

- Re-run the exact repro command from the issue and confirm the original
  failure is gone.
- `dotnet build` to confirm the change compiles clean (warnings are errors
  here).
- Run the narrowest relevant test first (`dotnet test --filter
  "FullyQualifiedName~X"`), then the full suite: `pwsh ./run-all-tests.ps1`.
- Since this is a compiler change, also run the broader gates CLAUDE.md
  requires for compiler changes:
  - `pwsh ./run-package-tests.ps1`
  - `pwsh ./build-examples.ps1`
- If the issue was fuzzer-found and you have time, do a small fresh fuzzer
  run touching the affected area to sanity-check the bug class is actually
  gone and you haven't introduced a new one — but don't block on a full
  1000-iteration run; a few hundred iterations is enough signal.
- If any verification step fails, fix forward — don't delete the issue file
  or commit until everything above is green.

## 5. Close out the issue

Once verified, delete the issue file you fixed (`issues/<file>.md`). Leave
the other issue files alone unless step 1 determined your fix also closes
one of them — in that case delete that one too and say so explicitly.

## 6. Commit

Per CLAUDE.md's git workflow: commit directly to the current branch (do not
create a new branch), and don't push unless asked. Stage the source fix and
the deleted issue file together. Write a commit message that describes the
bug that was fixed (feel free to draw on the issue's title/symptom), not a
restatement of the diff. Follow the repo's existing commit message style
(see `git log`).
