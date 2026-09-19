---
name: fuzz-triage
description: Run the ZScheme differential fuzzer and document genuinely new failure families in issues/. Use when asked to find compiler bugs or refresh the issue backlog; this workflow documents findings and does not fix them.
---

# Triage fuzzer failures

Read `docs/FUZZER.md` and existing `issues/*.md` reports first. Run:

```powershell
dotnet run --project src/ZScheme.Fuzzer -- --seed <seed> -n <iterations>
```

Always use an explicit seed. Default to 1,000 iterations unless the user gives
another count. Preserve and inspect one run's `session.json`, `cases.jsonl`, and
artifacts instead of rerunning it for different searches. If the run is clean,
report that and stop.

Cluster failures by verified root cause rather than message text or seed. For a
representative failure, inspect `report.json`, `original.zs`, generated output,
and the implicated compiler code. Compare each cluster's mechanism with existing
issues and known accepted gaps before writing a new report.

For each genuinely new cluster, add one kebab-case report in `issues/` following
the existing report structure: title, discovery/run location, impact, representative
seeds, a tested repro command, symptom, root cause with source locations,
optional confident fix direction, and a priority note. If preserving a generated
repro outside its run directory, copy all auxiliary modules with it.

Do not fix the failures during this workflow and do not commit the reports unless
the user explicitly asks. Summarize total failures, distinct root causes, tracked
clusters, and new filenames.
