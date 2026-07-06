# LSP Functionality Gaps

Analysis of `src/ZScheme.LanguageServer/` against the LSP feature set.

## Currently implemented

The server (OmniSharp LSP, `Program.cs`) wires up **7 capabilities**:

- Text sync (Full) + push diagnostics (`TextDocumentSyncHandler`)
- Hover (`HoverHandler`)
- Go-to-definition, cross-file (`DefinitionHandler`)
- Find references, cross-file (`ReferencesHandler`)
- Document symbols (`DocumentSymbolHandler`)
- Workspace symbols (`WorkspaceSymbolHandler`)
- Completion (`CompletionHandler`)

A one-time background workspace index scan runs at startup (`AnalysisService.InitializeWorkspace` / `ScanWorkspace`). This is a solid navigation core but is missing most of the "editing intelligence" half of LSP.

## Tier 1 — high impact, low effort (machinery already exists)

| Gap | Why it's low-effort here |
|---|---|
| **Rename** (`textDocument/rename` + `prepareRename`) | `ReferencesHandler` / `SymbolResolver` / `WorkspaceIndex.FindReferences` already compute every cross-file occurrence. Rename is ~"references → `WorkspaceEdit`". Biggest ROI. |
| **Document Highlight** (`textDocument/documentHighlight`) | Same reference data, scoped to the current file. Highlights all occurrences of the symbol under the cursor. Nearly free. |
| **Inlay Hints** (`textDocument/inlayHint`) | HM-inferred language — nodes already carry `ResolvedType`. Showing inferred types on `let`/`define`/params inline is one of the most valuable features for a type-inferred Scheme. Hover already proves the data is available. |
| **Signature Help** (`textDocument/signatureHelp`) | Function symbols carry `ZFuncType`. Parameter hints while typing a call `(foo …)`; the `(` trigger char is already registered for completion. |

## Tier 2 — meaningful features, moderate effort

- **Code Actions / Quick Fixes** (`textDocument/codeAction`): no quick-fixes at all — no "add missing import", "add match arm for missing case" (an `ExhaustivenessChecker` already exists), "remove unused binding". Biggest missing *category*.
- **Semantic Tokens** (`textDocument/semanticTokens`): highlighting today is regex/TextMate only. Semantic tokens would accurately distinguish types vs. constructors vs. functions vs. params.
- **Folding Ranges** (`textDocument/foldingRange`) and **Selection Ranges** (`textDocument/selectionRange`): structural editing over S-expressions is a natural fit, easy from the AST/parens.
- **Type Definition** (`textDocument/typeDefinition`) and **Implementation** (`textDocument/implementation`): jump from a value to its record/union type, or from `define-interface` to implementors (`define-class`). Union/interface/class decls are already in the index.
- **CodeLens** (`textDocument/codeLens`): "N references" over defs, or "run test" over `zunit` test macros.

## Tier 3 — weaknesses in existing features

- **Completion is static and un-scoped** (`CompletionHandler.cs`): always returns the *entire* keyword list + *all* current-document symbols regardless of prefix or cursor context (top-level vs. type position vs. inside a call). Critically, it **omits cross-file/imported symbols** even though the workspace index has them — you can navigate to a cross-file symbol but not complete it. Also: parameters/locals are excluded, no docs, no snippets, `ResolveProvider = false`.
- **Diagnostics are bare** (`Diagnostic.cs` = `Severity` / `Message` / `Span` only): no diagnostic **codes** (needed to attach code actions), no **tags** (`Unnecessary` / `Deprecated` for the faded-out unused-variable look), no **related information** (e.g. "other match arms here").
- **Hover has no documentation**: the compiler captures no doc comments (no `;;;` / leading-comment convention exists), so hover is type-only.

## Tier 4 — infrastructure / correctness gaps

- **No file-watching** (`workspace/didChangeWatchedFiles`): the workspace index is scanned **once at startup** and only refreshed for *open* buffers. Edits to unopened files, new files, deletions, branch switches, or `git pull` all leave the cross-file index **stale** until server restart. This is a correctness bug for go-to-def / references / workspace-symbols, not just a missing feature.
- **No `didChangeWorkspaceFolders`**, no **work-done progress** (the startup scan is invisible — no indexing indicator), no **`workspace/executeCommand`** (blocks command-backed code actions / codelens).
- Missing the rest of the navigation family: **Declaration**, **Call Hierarchy**, **Type Hierarchy**, **Document Links** (clickable `import` paths), **Moniker**, **Linked Editing**.
- **No formatter**: there is no ZScheme source formatter anywhere in the compiler/CLI (the `format` hits are all C#-output emission), so `textDocument/formatting` / range / on-type formatting can't be offered without building one.

## Recommended order

1. **Rename + Document Highlight + Inlay Hints** — highest value for least code; all reuse existing `WorkspaceIndex`, `SymbolResolver`, and `ResolvedType`.
2. **Fix file-watching staleness** — a real correctness bug.
3. **Broaden completion** — include imported symbols + prefix filtering.
4. **Code actions** (starting with exhaustiveness quick-fixes) — biggest strategic gap, but needs diagnostic codes added to `Diagnostic.cs` first.
