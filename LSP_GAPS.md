# LSP Functionality Gaps

Analysis of `src/ZScheme.LanguageServer/` against the LSP feature set.

## Currently implemented

The server (OmniSharp LSP, `Program.cs`) wires up **11 capabilities**:

- Text sync (Full) + push diagnostics (`TextDocumentSyncHandler`)
- Hover (`HoverHandler`)
- Go-to-definition, cross-file (`DefinitionHandler`)
- Find references, cross-file (`ReferencesHandler`)
- Document symbols (`DocumentSymbolHandler`)
- Workspace symbols (`WorkspaceSymbolHandler`)
- Completion (`CompletionHandler`)
- Rename + prepareRename, cross-file (`RenameHandler` / `PrepareRenameHandler`)
- Document highlight (`DocumentHighlightHandler`)
- Inlay hints — inferred types on bindings, params, and return types (`InlayHintHandler`)
- Signature help, with overloads (`SignatureHelpHandler`)

A one-time background workspace index scan runs at startup (`AnalysisService.InitializeWorkspace` / `ScanWorkspace`). The four Tier 1 "editing intelligence" features above are now implemented; the remaining gaps are below.

Deferred within these features (follow-ups): rename/highlight of a local variable is matched by bare name within the file, so it over-selects shadowed locals of the same name; rename cannot yet be initiated from a record/union/class/interface *declaration* name (only from a usage), since those decl names aren't synthesized as `Name` nodes; inlay hints don't emit call-site parameter-name hints and signature-help parameter labels are types only, both because `ZFuncType` carries no parameter names.

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

1. ~~**Rename + Document Highlight + Inlay Hints + Signature Help**~~ — **done** (all four Tier 1 features implemented, reusing `WorkspaceIndex`, `SymbolResolver`, and `ResolvedType`).
2. **Fix file-watching staleness** — a real correctness bug.
3. **Broaden completion** — include imported symbols + prefix filtering.
4. **Code actions** (starting with exhaustiveness quick-fixes) — biggest strategic gap, but needs diagnostic codes added to `Diagnostic.cs` first.
