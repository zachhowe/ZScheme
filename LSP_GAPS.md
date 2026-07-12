# LSP Functionality Gaps

Analysis of `src/ZScheme.LanguageServer/` against the LSP feature set.

## Currently implemented

The server (OmniSharp LSP, `Program.cs`) wires up **21 capabilities**:

- Text sync (Full) + push diagnostics with codes, structured data, **tags** (ZS0003 → `Unnecessary`), and **related information** (`TextDocumentSyncHandler`)
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
- Code actions — add missing match arms (ZS0002), add missing import (ZS0001), prefix-with-underscore + remove unused binding (ZS0003) (`CodeActionHandler`)
- Folding ranges — multi-line forms + comment blocks, purely lexical (`FoldingRangeHandler`)
- Selection ranges — S-expression expansion chains, atom → interior → form (`SelectionRangeHandler`)
- Semantic tokens (full) — three merged layers: lexical (comments/strings/numbers/head-position keywords), type positions, typed-AST names incl. match patterns (`SemanticTokensHandler`)
- Type definition — value → its record/union/class/interface/alias declaration (`TypeDefinitionHandler`)
- Implementation — interface → implementing classes/extending interfaces (transitive; also class → subclasses), via a `WorkspaceIndex` implementations facet (`ImplementationHandler`)
- Document links — clickable module names in `(import …)`, resolved with the compiler's search-path/package/alias setup (`DocumentLinkHandler`)
- CodeLens — "N references" over top-level definitions (`CodeLensHandler`; informational, not clickable — see below)
- Work-done progress — the startup workspace scan reports begin/percentage/end via `window/workDoneProgress` (`WorkspaceScanProgressReporter`)

A one-time background workspace index scan runs at startup (`AnalysisService.InitializeWorkspaceAsync` / `ScanWorkspace`), kept current afterwards by the file watcher (plus a disk re-sync on `didClose`). Since the scan now materializes its work list first, it reports client-visible progress. Shared lexical infrastructure lives in `Analysis/LexicalStructure.cs` (token-level bracket tree with true multi-line extents; the lexer's `Tokenize(keepComments: true)` retains comment tokens) — this is what folding/selection/semantic tokens/document links use instead of AST spans, since `SourceSpan` is single-line.

The compiler now emits:

- **ZS0002 as an Error** (union-case non-exhaustiveness — sound; verified clean across stdlib/packages/examples). The Bool and literal-heuristic checks remain Warnings. ZS0002 carries "existing arm here" related information per arm.
- **ZS0003 unused-binding Warnings** (`Types/UnusedBindingAnalyzer.cs`, pipeline stage 4.6): scope-aware occurrence counting over `let`/`use` locals, `_`-prefix opt-out, desugared/macro-synthesized bindings skipped (`Let`/`Use` now carry a `NameSpan`). Rendered greyed-out via the `Unnecessary` tag.
- `Diagnostic.Related` (`DiagnosticRelatedInfo` list), forwarded as LSP related information.

Rename, document highlight, and completion are now **scope-aware for locals** (`Analysis/ScopeAnalysis.cs`): occurrences of a `let`/`use` variable, parameter, or match-pattern variable are resolved by walking the AST's binding structure (shadowing rules mirror the compiler's `UnusedBindingAnalyzer.IsUsed`), so shadowed locals of the same name no longer over-select in either direction, rename can be initiated from a binding site (previously the `let` binding name wasn't even renamed), and completion offers locals only within their form's extent (via the lexical bracket tree). Completion is also **context-aware**: in type positions (`Analysis/TypePosition.cs` — after `:`, inside a type expression, after `new`/`typeof`) it offers only type names; elsewhere it suppresses the type-only builtins. `Param` now carries a `NameSpan` (its `Span` covers the whole `[name : Type]` bracket — renaming a typed parameter used to replace the entire bracket, silently dropping the annotation).

Rename (and hover/references/highlight) can now be initiated from record/union/class/interface **declaration names and union case names**: those decls carry a `NameSpan` (compiler-side, mirroring `Define`/`Let`) and `AstNavigation` synthesizes `Name` nodes for them, so indexed definition spans point at the name atom instead of the whole form (which also tightens CodeLens ranges and decl-site rename edits).

Inlay hints now emit **call-site parameter-name hints** (`factor:` before each argument) and signature-help labels are **`name : Type`**: rather than threading names through `ZFuncType` (~24 inference construction sites), `IndexedDefinition` carries a `ParamNames` facet and `Analysis/ParamNameResolver.cs` resolves names from the same-file AST first, then the index — returning null (type-only labels, no hints) whenever candidates disagree, so a wrong name is never shown. Hints are suppressed when the argument is a variable already named like the parameter, for `_`-prefixed parameters, and in variadic tails.

Deferred within these features (follow-ups): renaming a type does not rewrite type-annotation positions (`[x : Point]`, `: Point` return types) — type spans don't survive into `ZType`, so annotation sites aren't in the reference index; rename/highlight decline on `with-handlers` binding variables (`HandlerClause` carries no name span); CodeLens titles aren't clickable (peek-references needs `workspace/executeCommand` or the client-specific `editor.action.showReferences`); the "remove unused binding" fix is not offered inside `let*` (its desugared `Let` nodes share one form span — the underscore-prefix fix covers it); unused-binding analysis covers `let`/`use` locals only (parameters and top-level defines need an export-awareness story first); semantic tokens are full-document only (no delta/range); completion still has no docs or snippets and `ResolveProvider = false`.

## Tier 2 — meaningful features, moderate effort

- ~~**Code Actions / Quick Fixes**~~ — **done**: "add missing match arms" (ZS0002), "add missing import" (ZS0001), and "prefix with underscore" / "remove unused binding" (ZS0003; the remove fix replaces the form with its body when the bound value is pure, else rewrites to `(begin …)`).
- ~~**Semantic Tokens**~~ — **done** (`textDocument/semanticTokens/full`; delta/range not offered).
- ~~**Folding Ranges** and **Selection Ranges**~~ — **done** (lexical bracket tree, so both work mid-edit on unbalanced source).
- ~~**Type Definition** and **Implementation**~~ — **done** (implementation uses a new interface→implementors index facet; the class `: Base IFoo` name list is indexed whole since the AST can't split base class from interfaces).
- ~~**CodeLens**~~ — **done** ("N references"; clickable peek deferred on `executeCommand`).

## Tier 3 — weaknesses in existing features

- ~~**Completion is static and un-scoped**~~ — **done**: server-side prefix filtering, cross-file/imported symbols from the workspace index (module shown as detail, sorted after same-file symbols), scope-filtered params/locals, and type-vs-expression context awareness. Still no docs, no snippets, `ResolveProvider = false` (see deferred list above).
- ~~**Diagnostics are bare**~~ — **done**: codes + structured data (ZS0001–ZS0003), tags (`Unnecessary` on ZS0003), and related information ("existing arm here" on ZS0002).
- **Hover has no documentation**: the compiler captures no doc comments (no `;;;` / leading-comment convention exists), so hover is type-only. Deferred deliberately — designing the doc-comment convention is a language-level effort. Note the lexer can now retain comment tokens (`Tokenize(keepComments: true)`), which is the first prerequisite.

## Tier 4 — infrastructure / correctness gaps

- ~~**No file-watching**~~ — **done**: `DidChangeWatchedFilesHandler` watches `**/*.zs` + `**/*.zspkg`; creates/changes queue a coalesced re-index (500 ms quiet period, open buffers win over disk), deletes purge the index, manifest changes re-index the package's files, and `didClose` re-syncs from disk.
- ~~No **work-done progress**~~ — **done** for the startup scan (`IWorkspaceScanReporter` keeps `AnalysisService` LSP-free; `WorkspaceScanProgressReporter` implements it over `IServerWorkDoneManager`).
- **No `didChangeWorkspaceFolders`**, no **`workspace/executeCommand`** (blocks command-backed code actions / clickable codelens; the current quick fixes use inline `WorkspaceEdit`s, which need no commands).
- Missing the rest of the navigation family: **Declaration**, **Call Hierarchy**, **Type Hierarchy**, **Moniker**, **Linked Editing**. ~~Document Links~~ — **done** (clickable `import` module names).
- **Formatter**: a ZScheme source formatter is **in progress on another branch**; `textDocument/formatting` / range / on-type formatting will be wired to it when it lands.

## Recommended order

1. ~~**Rename + Document Highlight + Inlay Hints + Signature Help**~~ — **done**.
2. ~~**Fix file-watching staleness**~~ — **done**.
3. ~~**Broaden completion**~~ — **done**.
4. ~~**Code actions**~~ — **done**.
5. ~~**Semantic tokens, folding/selection ranges, type definition/implementation, document links, codelens, workspace-scan progress, unused-binding analysis (ZS0003 + `Unnecessary` tag + quick fixes), diagnostic related-info, ZS0002 → Error**~~ — **done**.

6. ~~**Scope-aware rename/highlight/completion + context-aware completion**~~ — **done** (`ScopeAnalysis` + `TypePosition`; `Param.NameSpan` added compiler-side).
7. ~~**Rename from type-declaration names**~~ — **done** (`NameSpan` on `RecordDecl`/`UnionDecl`/`ClassDecl`/`InterfaceDecl`/`UnionCase`).
8. ~~**Call-site parameter-name inlay hints + named signature labels**~~ — **done** (`ParamNames` index facet + `ParamNameResolver`).

Next candidates: hover documentation (needs a doc-comment convention — language design first), call/type hierarchy, clickable CodeLens via `editor.action.showReferences`, unused parameters/top-level defines (ZS0003), `let*` remove-binding fix, semantic-token delta/range, `didChangeWorkspaceFolders`, rename covering type-annotation positions (needs type-position spans), formatting handlers once the formatter branch lands.
