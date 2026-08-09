---
name: release
description: Cut a ZScheme release — bring docs/changelog/unreleased.md fully up to date with every commit since it was last touched, rename it to the version being released, update the changelog index, commit, and tag. Use whenever the user asks to cut/tag a release, "release X.Y.Z", refresh or catch up the changelog, or asks what's gone unrecorded since the last release.
argument-hint: [version, optional — defaults to the version in Directory.Build.props]
---

# Release

Turn the commits accumulated since the last changelog update into a released
version: a complete `docs/changelog/<version>.md`, an updated index, one commit,
and a tag on it.

The changelog lives in `docs/changelog/` — one file per release, plus
`README.md` (the index table) and `unreleased.md` (the in-progress entry).
Read two or three existing entries before writing anything; they establish the
voice and structure you must match.

If the user only asked to refresh the changelog and said nothing about
releasing, do steps 1–3 and stop. Steps 4–7 perform Git operations, which
CLAUDE.md forbids unless explicitly asked — cutting a release is that ask.

## 1. Find the boundary

The changelog is up to date as of the last commit that touched
`unreleased.md`. Find it, and everything after it is your sweep:

```bash
git log -1 --format='%H %s' -- docs/changelog/unreleased.md
git log --oneline --reverse <sha>..HEAD
```

Two things to check before trusting that boundary:

- **If `unreleased.md` has never been committed**, fall back to the last tag
  (`git describe --tags --abbrev=0`) and sweep from there.
- **If the boundary commit touched anything outside `docs/changelog/`**
  (`git show --stat <sha>`), it carried real work that may not be recorded —
  include that commit in the sweep too.

Also check `git status`. Uncommitted work in the tree is *not* in the sweep and
will not be in the release; say so explicitly rather than silently releasing
around it.

Say how many commits you're sweeping and what the boundary was, in one line.

## 2. Read the commits, not just the subjects

Subject lines are a starting point, not the entry. For anything whose subject
is vague, ambiguous, or which sounds larger than one line ("Fix compiler bug",
"Cleanup", "Wire up X"), read the actual diff (`git show <sha>`) before
describing it. Getting a release note wrong is worse than omitting it.

While reading, sort each commit into one of:

- **Breaking** — renamed or removed forms, changed syntax, changed defaults,
  moved packages. These matter most to a reader; never bury them.
- **Added** — new forms, stdlib functions, packages, CLI flags, diagnostics,
  LSP features, tooling.
- **Changed** — behaviour or internals that a reader would notice, plus
  significant refactors (a pass moving between stages, a backend being replaced).
- **Fixed** — group by subsystem when there are many. The existing entries use
  headings like "Fixed — IL backend", "Fixed — C# backend", "Fixed — type
  inference"; follow that when a release has more than ~10 fixes.

Drop pure noise: formatting commits, typo fixes in comments, "Cleanup" commits
with no behavioural change, version bumps, and changelog commits themselves.
Collapse a chain of commits that iterate toward one outcome (e.g. eight commits
converging on "aspnet tests pass") into the single thing that landed.

## 3. Write `unreleased.md` up to date

Merge the new material into the existing `unreleased.md` — don't start over,
and don't lose entries already recorded there. Match the conventions of the
released entries:

- A short lead paragraph naming the one or two themes of the release. If a
  release has an obvious character (0.1.6 was "the fuzzer release"), say so.
- `##` sections in this order, omitting empty ones: Breaking changes, Added
  (split with `— language` / `— packages` / `— tooling` suffixes when long),
  Changed, Fixed, plus any release-specific section that earns its place.
- Bold the subject of each bullet; write what changed and why it matters, not
  what the diff did.
- Reference forms in code spans exactly as a user would type them
  (`define-async`, `(with ...)`, `import-clr :instance`).

Cross-check against the repo state: if a bullet claims a form, flag, or file
exists, confirm it still does. A commit that added something and a later commit
that removed it should net out to nothing in the entry.

## 4. Confirm the version and tag name

The version being released is already in `Directory.Build.props` — this repo
bumps at the *start* of a cycle, not at release time. Read it:

```bash
grep '<Version>' Directory.Build.props
```

Use the user's argument if they gave one; otherwise use that value. Tag names
in this repo have **no `v` prefix**, and a trailing `.0` is dropped (version
`0.2.0` was tagged `0.2`; `0.1.1` was tagged `0.1.1`). Confirm against
`git tag --sort=creatordate` and state the tag name you intend to use before
creating it. If the sweep contains breaking changes but the version is only a
patch bump, say so and ask before proceeding.

## 5. Verify the tree is green

A tag is permanent; don't put one on a broken commit. Per CLAUDE.md, save
output to a temp file and grep that rather than re-running with different
filters:

- `dotnet build` (warnings are errors here)
- `pwsh ./run-all-tests.ps1`
- `pwsh ./run-package-tests.ps1`
- `pwsh ./build-examples.ps1`

If the user just ran these and nothing has changed since, say you're relying on
that instead of re-running. If anything fails, stop — report it and do not tag.

## 6. Promote and commit

- `git mv docs/changelog/unreleased.md docs/changelog/<version>.md`
  (use the full version in the filename, matching the existing files).
- Change its heading from `# X.Y.Z (unreleased)` to `# <version> — <today>`,
  and drop any "in development since" phrasing from the lead paragraph.
- Update the table in `docs/changelog/README.md`: turn the unreleased row into
  the released row with its date, commit count
  (`git rev-list --count <prev-tag>..HEAD`), and a short theme phrase, then add
  a fresh unreleased row above it for the next version.
- Commit **only the changelog files**, directly to the current branch (CLAUDE.md:
  never create a branch, never push unless asked). Message style: match
  `git log` — a plain statement of what the commit does, e.g.
  `Release <version>`.

## 7. Tag it

```bash
git tag <tag-name>
```

Lightweight, matching every existing tag in this repo. Verify with
`git tag --sort=creatordate | tail -3` and `git log -1 --oneline <tag-name>`.

Do **not** push the commit or the tag. Tell the user the tag exists locally and
what to run to push it (`git push origin <branch> && git push origin <tag>`) —
pushing a tag is effectively irreversible and is their call.

## 8. Open the next cycle

This repo bumps immediately after tagging, so the next cycle's commits carry
the next version. As a **separate commit after the tag**:

- `pwsh ./bump-version.ps1 <next-version>` — updates `Directory.Build.props`
  and the two editor `package.json` files.
- Bump any package whose contents changed this cycle, one at a time:
  `pwsh ./bump-package-version.ps1 -Package <name> -Version <next-version>`.
  Packages are versioned independently — only `stdlib` moved to 0.3.0 during
  the 0.3.0 cycle, for instance. Leave untouched packages alone.
- Create a fresh `docs/changelog/unreleased.md` with the
  `# <next-version> (unreleased)` heading and nothing under it yet.
- Commit as e.g. `Bump version to <next-version>`.

Ask which next version to use if it isn't obvious from the release you just
cut (patch for a fix-only release, minor when the cycle added or broke
anything).
