# LSP Functionality Gaps

Analysis of `src/ZScheme.LanguageServer/` against the LSP feature set.

## Currently implemented

The server (OmniSharp LSP, `Program.cs`) wires up **13 capabilities**:

- Text sync (Full) + push diagnostics with codes + structured data (`TextDocumentSyncHandler`)
- Hover (`HoverHandler`)
- Go-to-definition, cross-file (`DefinitionHandler`)
- Find references, cross-file (`ReferencesHandler`)
- Document symbols (`DocumentSymbolHandler`)
- Workspace symbols (`WorkspaceSymbolHandler`)
- Completion — prefix-filtered, cross-file/imported symbols, params/locals (`CompletionHandler`)
- Rename + prepareRename, cross-file (`RenameHandler` / `PrepareRenameHandler`)
- Document highlight (`DocumentHighlightHandler`)
- Inlay hints — inferred types on bindings, params, and return types (`InlayHintHandler`)
- Signature help, with overloads (`SignatureHelpHandler`)
- File watching — index stays fresh on external edits/creates/deletes, coalesced (`DidChangeWatchedFilesHandler`)
- Code actions — add missing match arms (ZS0002), add missing import (ZS0001) (`CodeActionHandler`)

A one-time background workspace index scan runs at startup (`AnalysisService.InitializeWorkspace` / `ScanWorkspace`), kept current afterwards by the file watcher (plus a disk re-sync on `didClose`). The compiler's `ExhaustivenessChecker` is now wired into type inference (post-inference `Resolve`, imported unions included) and emits **ZS0002 warnings** carrying the missing cases as structured data; `Diagnostic` gained optional `Code`/`Data` (`DiagnosticCodes.cs`) and the CLI now prints warnings on successful compiles.

Deferred within these features (follow-ups): rename/highlight of a local variable is matched by bare name within the file, so it over-selects shadowed locals of the same name; rename cannot yet be initiated from a record/union/class/interface *declaration* name (only from a usage), since those decl names aren't synthesized as `Name` nodes; inlay hints don't emit call-site parameter-name hints and signature-help parameter labels are types only, both because `ZFuncType` carries no parameter names; completion is scope-flat (params/locals are offered file-wide — single-line `SourceSpan`s carry no scope extents) and not context-aware (type vs. expr position); the "remove unused binding" quick fix needs a net-new unused-binding analysis (ZS0003 is reserved); ZS0002 stays a Warning until the ecosystem is verified clean, then can be promoted to Error.

## Tier 2 — meaningful features, moderate effort

- ~~**Code Actions / Quick Fixes**~~ — **done** for the first two fixes: "add missing match arms" (from ZS0002's structured missing-case data) and "add missing import" (ZS0001 + `WorkspaceIndex`). "Remove unused binding" still needs an unused-binding analysis pass (ZS0003 reserved).
- **Semantic Tokens** (`textDocument/semanticTokens`): highlighting today is regex/TextMate only. Semantic tokens would accurately distinguish types vs. constructors vs. functions vs. params.
- **Folding Ranges** (`textDocument/foldingRange`) and **Selection Ranges** (`textDocument/selectionRange`): structural editing over S-expressions is a natural fit, easy from the AST/parens.
- **Type Definition** (`textDocument/typeDefinition`) and **Implementation** (`textDocument/implementation`): jump from a value to its record/union type, or from `define-interface` to implementors (`define-class`). Union/interface/class decls are already in the index.
- **CodeLens** (`textDocument/codeLens`): "N references" over defs, or "run test" over `zunit` test macros.

## Tier 3 — weaknesses in existing features

- ~~**Completion is static and un-scoped**~~ — **mostly done**: server-side prefix filtering, cross-file/imported symbols from the workspace index (module shown as detail, sorted after same-file symbols), parameters/locals included. Still no docs, no snippets, no context-awareness (type vs. expr position), `ResolveProvider = false`, and scoping is flat (see deferred list above).
- ~~**Diagnostics are bare**~~ — **partially done**: `Diagnostic` now carries optional `Code` + structured `Data` (forwarded to LSP clients), assigned to ZS0001/ZS0002. Still no **tags** (`Unnecessary` / `Deprecated`) and no **related information** (e.g. "other match arms here").
- **Hover has no documentation**: the compiler captures no doc comments (no `;;;` / leading-comment convention exists), so hover is type-only.

## Tier 4 — infrastructure / correctness gaps

- ~~**No file-watching**~~ — **done**: `DidChangeWatchedFilesHandler` watches `**/*.zs` + `**/*.zspkg`; creates/changes queue a coalesced re-index (500 ms quiet period, open buffers win over disk), deletes purge the index, manifest changes re-index the package's files, and `didClose` re-syncs from disk.
- **No `didChangeWorkspaceFolders`**, no **work-done progress** (the startup scan is invisible — no indexing indicator), no **`workspace/executeCommand`** (blocks command-backed code actions / codelens; the current quick fixes use inline `WorkspaceEdit`s, which need no commands).
- Missing the rest of the navigation family: **Declaration**, **Call Hierarchy**, **Type Hierarchy**, **Document Links** (clickable `import` paths), **Moniker**, **Linked Editing**.
- **No formatter**: there is no ZScheme source formatter anywhere in the compiler/CLI (the `format` hits are all C#-output emission), so `textDocument/formatting` / range / on-type formatting can't be offered without building one.

## Recommended order

1. ~~**Rename + Document Highlight + Inlay Hints + Signature Help**~~ — **done** (all four Tier 1 features implemented, reusing `WorkspaceIndex`, `SymbolResolver`, and `ResolvedType`).
2. ~~**Fix file-watching staleness**~~ — **done** (`DidChangeWatchedFilesHandler` + `AnalysisService.ReindexFromDisk`/`QueueReindexAsync`).
3. ~~**Broaden completion**~~ — **done** (prefix filtering, `WorkspaceIndex.CompletionCandidates`, params/locals).
4. ~~**Code actions**~~ — **done** (diagnostic codes ZS0001/ZS0002 in the compiler, `ExhaustivenessChecker` wired into the pipeline as Warnings, `CodeActionHandler` with add-missing-arms + add-import fixes).

Next candidates: semantic tokens, folding/selection ranges, type definition / implementation, work-done progress for the startup scan, unused-binding analysis (unlocks ZS0003 + the `Unnecessary` tag), promoting ZS0002 to Error.
