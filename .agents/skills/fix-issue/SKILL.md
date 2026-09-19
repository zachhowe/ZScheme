---
name: fix-issue
description: Investigate and fix a documented ZScheme bug from issues/. Use when asked to work through the issue backlog, fix a fuzzer-found bug, or target a named issue; do not use for merely documenting fuzz failures.
---

# Fix a documented issue

Read all `issues/*.md` files. If the request identifies one by name or keyword,
use it; otherwise prefer explicitly higher-priority, real correctness bugs with
the broadest impact. Read linked issues before selecting one. State the selected
issue and reason before changing code.

Before implementing, read the source cited by the report and run its exact repro
command. Treat the report's root-cause analysis as a lead, not a fact. Make the
smallest root-cause fix, preserving backend scope: do not alter the other backend
unless it is affected.

Verify by rerunning the repro, building, and running focused then broader tests.
For compiler changes, follow the verification gates in `AGENTS.md`. A short fresh
fuzzer run can validate a fuzzer-found issue, but should not replace the repro.

Only after all required verification succeeds, remove the resolved issue file;
leave other reports alone unless the same fix demonstrably closes them. Commit
only when the user explicitly asked to commit. Stage the implementation and the
resolved report together, use the repository's commit-message style, and do not
push unless asked.
