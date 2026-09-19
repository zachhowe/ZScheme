---
name: release
description: Prepare or cut a ZScheme release by reconciling the changelog with commits, then optionally committing and tagging. Use for release requests, changelog catch-up, or identifying unrecorded changes.
---

# Prepare a release

Read two or three prior entries in `docs/changelog/`. Find the most recent
commit touching `docs/changelog/unreleased.md`; sweep commits after it. If it
has never been committed, use the latest tag. Include the boundary commit when
it also contains non-changelog work, and call out uncommitted work separately.

Read diffs for vague or substantial commit subjects. Merge only user-visible,
net changes into `unreleased.md`; omit noise, version bumps, and changelog-only
commits. Match existing prose and section ordering. Confirm referenced forms,
flags, and files still exist.

If the request is changelog-only, stop after updating `unreleased.md`. For a
release, read `Directory.Build.props`; use an explicitly supplied version or its
version exactly. Tag names are full `X.Y.Z` with no `v` prefix. Flag a patch
release containing breaking changes before proceeding.

Before tagging, run the build and appropriate full test gates from `AGENTS.md`
unless the user confirms equivalent checks ran after the last relevant change.
Do not tag if they fail. When authorized to release, promote `unreleased.md` to
`docs/changelog/<version>.md`, date its heading, update the changelog index,
commit only changelog files to the current branch, and create a lightweight local
tag. Do not push the commit or tag unless explicitly requested.

After a tag, bump the shared version with `bump-version.ps1 <next-version>`,
start a fresh `unreleased.md`, and commit that as a separate follow-up only when
the user has authorized the next-cycle version choice.
