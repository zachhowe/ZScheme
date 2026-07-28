# LSP Functionality Gaps

Analysis of `src/ZScheme.LanguageServer/` against the LSP feature set.

## Currently implemented

The server (OmniSharp LSP, `Program.cs`) wires up **23 capabilities**:

- Text sync (Full) + push diagnostics with codes, structured data, **tags** (ZS0003, ZS0004 → `Unnecessary`), and **related information** (`TextDocumentSyncHandler`)
- Hover (`HoverHandler`)
- Go-to-definition, cross-file (`DefinitionHandler`) — top-level symbols via the workspace index, **locals scope-aware** (parameters, `let`/`use` names, match-pattern variables) via `ScopeAnalysis.BindingSiteAt`, and `import-clr` aliases (which now carry an `AliasSpan` and are indexed like any other module-scope binding)
- Go-to-declaration (`DeclarationHandler`) — delegates to `DefinitionHandler`: ZScheme has no declaration/definition split, but the request previously failed with `Method not found`
- Find references, cross-file (`ReferencesHandler`)
- Document symbols (`DocumentSymbolHandler`)
- Workspace symbols (`WorkspaceSymbolHandler`)
- Completion — prefix-filtered, cross-file/imported symbols, params/locals (`CompletionHandler`)
- Rename + prepareRename, cross-file (`RenameHandler` / `PrepareRenameHandler`)
- Document highlight (`DocumentHighlightHandler`)
- Inlay hints — inferred types on bindings, params, and return types (`InlayHintHandler`)
- Signature help, with overloads (`SignatureHelpHandler`)
- File watching — index stays fresh on external edits/creates/deletes, coalesced (`DidChangeWatchedFilesHandler`)
- Code actions — add missing match arms (ZS0002), add missing import (ZS0001), prefix-with-underscore + remove unused binding (ZS0003), simplify a redundantly qualified type name (ZS0004) (`CodeActionHandler`)
- Folding ranges — multi-line forms + comment blocks, purely lexical (`FoldingRangeHandler`)
- Selection ranges — S-expression expansion chains, atom → interior → form (`SelectionRangeHandler`)
- Semantic tokens (full + **delta** + **range**) — three merged layers: lexical (comments/strings/numbers/head-position keywords), type positions, typed-AST names incl. match patterns; delta encoding and range clipping ride OmniSharp's base over a per-URI token-document cache (`SemanticTokensHandler`)
- Type definition — value → its record/union/class/interface/alias declaration (`TypeDefinitionHandler`)
- Implementation — interface → implementing classes/extending interfaces (transitive; also class → subclasses), via a `WorkspaceIndex` implementations facet (`ImplementationHandler`)
- Document links — clickable module names in `(import …)`, resolved with the compiler's search-path/package/alias setup (`DocumentLinkHandler`)
- CodeLens — "N references" over top-level definitions, clickable: the lens carries the client-side `editor.action.showReferences` command (uri, position, locations — the rust-analyzer pattern; clients that don't know it render plain text) (`CodeLensHandler`)
- Workspace-folder changes — added folders are scanned in the background, removed folders purged from the index (`WorkspaceFoldersHandler`)
- Work-done progress — the startup workspace scan reports begin/percentage/end via `window/workDoneProgress` (`WorkspaceScanProgressReporter`)
- Formatting (document + range) — runs `ZScheme.Formatter` over the live buffer and returns line-granular edits (`DocumentFormattingHandler` / `DocumentRangeFormattingHandler`, sharing `FormattingSupport`)

All capabilities are advertised **statically**, in the `initialize` result (`StaticCapabilities`, hooked from `Program.cs` via `OnInitialize`). OmniSharp otherwise picks dynamic registration whenever the client claims `dynamicRegistration` support, and not every client honours the resulting `client/registerCapability` — Zed claims support and then logs `unhandled capability registration: textDocument/didOpen`, so it never opened documents with the server and saw no definition provider, which made *every* navigation request silently resolve to nothing. Our registration options are constant (a fixed document selector), so dynamic registration bought us nothing.

A one-time background workspace index scan runs at startup (`AnalysisService.InitializeWorkspaceAsync` / `ScanWorkspace`), kept current afterwards by the file watcher (plus a disk re-sync on `didClose`). Since the scan now materializes its work list first, it reports client-visible progress. Shared lexical infrastructure lives in `Analysis/LexicalStructure.cs` (token-level bracket tree with true multi-line extents; the lexer's `Tokenize(keepComments: true)` retains comment tokens) — this is what folding/selection/semantic tokens/document links use instead of AST spans, since `SourceSpan` is single-line.

The compiler now emits:

- **ZS0002 as an Error** (union-case non-exhaustiveness — sound; verified clean across stdlib/packages/examples). The Bool and literal-heuristic checks remain Warnings. ZS0002 carries "existing arm here" related information per arm.
- **ZS0003 unused-binding Warnings** (`Types/UnusedBindingAnalyzer.cs`, pipeline stage 4.6): scope-aware occurrence counting over `let`/`use` locals, **parameters** (define/lambda/methods/constructors — disable per package via the manifest's `(build (main (warn-unused-params "false")))` or per invocation via `--no-warn-unused-params`), and **unused private top-level defines** (only in programs with an `(export …)` form; `main`, attribute-carrying, and `_`-prefixed defines exempt; self-recursion doesn't count as use). `_`-prefix opt-out throughout; desugared/macro-synthesized bindings skipped (`Let`/`Use`/`Param` carry a `NameSpan`). Rendered greyed-out via the `Unnecessary` tag. Stdlib/packages/examples were swept clean — the sweep caught two stdlib helpers that were never exported (`compose/call`, `make-error-with-inner`, now exported) and that `http/post`/`http/put`/`http/post-json` silently ignore their `headers` parameter (marked TODO).
- `Diagnostic.Related` (`DiagnosticRelatedInfo` list), forwarded as LSP related information.

The server also emits one diagnostic that no compile path produces, at the `DiagnosticSeverity.Hint` level (so `zs build` output is unaffected; `zs lint` opts into the same analyzer):

- **ZS0004 redundant type qualifier** (`ZScheme.Compiler/Analysis/RedundantTypeQualifierAnalyzer.cs`, shared with `zs lint`): a fully-qualified CLR type name whose namespace the same file declares with `(import-clr Ns …)`, so the short name resolves to the identical type. The span covers only the `Ns.` characters — greyed out via the `Unnecessary` tag, and the quick fix is a plain deletion. Soundness comes from asking the compilation's own `TypeNameCanonicalizer` whether both spellings canonicalize to the same name, which automatically declines when a ZScheme type shadows the short name, when two imported namespaces both define it, or when the assembly can't be resolved. Namespaces inherited from an imported module's `ExportedClrNamespaces` deliberately don't count: the justification has to be visible in the file. Since type annotations have no spans (see the deferred item below), the analyzer works off the token stream — `ZScheme.Compiler/Analysis/TypeNameScanner.cs` re-walks the type grammar to find every type-position name and its generic arity.

Rename, document highlight, and completion are now **scope-aware for locals** (`Analysis/ScopeAnalysis.cs`): occurrences of a `let`/`use` variable, parameter, or match-pattern variable are resolved by walking the AST's binding structure (shadowing rules mirror the compiler's `UnusedBindingAnalyzer.IsUsed`), so shadowed locals of the same name no longer over-select in either direction, rename can be initiated from a binding site (previously the `let` binding name wasn't even renamed), and completion offers locals only within their form's extent (via the lexical bracket tree). Completion is also **context-aware**: in type positions (`Analysis/TypePosition.cs` — after `:`, inside a type expression, after `new`/`typeof`) it offers only type names; elsewhere it suppresses the type-only builtins. `Param` now carries a `NameSpan` (its `Span` covers the whole `[name : Type]` bracket — renaming a typed parameter used to replace the entire bracket, silently dropping the annotation).

Rename (and hover/references/highlight) can now be initiated from record/union/class/interface **declaration names and union case names**: those decls carry a `NameSpan` (compiler-side, mirroring `Define`/`Let`) and `AstNavigation` synthesizes `Name` nodes for them, so indexed definition spans point at the name atom instead of the whole form (which also tightens CodeLens ranges and decl-site rename edits).

Inlay hints now emit **call-site parameter-name hints** (`factor:` before each argument) and signature-help labels are **`name : Type`**: rather than threading names through `ZFuncType` (~24 inference construction sites), `IndexedDefinition` carries a `ParamNames` facet and `Analysis/ParamNameResolver.cs` resolves names from the same-file AST first, then the index — returning null (type-only labels, no hints) whenever candidates disagree, so a wrong name is never shown. Hints are suppressed when the argument is a variable already named like the parameter, for `_`-prefixed parameters, and in variadic tails.

Deferred within these features (follow-ups): renaming a type does not rewrite type-annotation positions (`[x : Point]`, `: Point` return types) — type spans don't survive into `ZType`, so annotation sites aren't in the reference index; rename/highlight decline on `with-handlers` binding variables (`HandlerClause` carries no name span); completion still has no docs or snippets and `ResolveProvider = false`; a "remove unused parameter" quick fix (arity change + call-site rewrites) is not offered — the underscore-prefix fix covers unused parameters.

Navigation gaps that remain (deliberate, deferred): go-to-definition declines on **constructor names inside match patterns** (`(Circle r)`, `Nil`) — `MatchArm.Pattern` is not part of `AstNavigation.Children` and `Pattern.Constructor` carries no name span, so only the pattern's *variables* navigate; and on **type names in annotations** (`: Shape`, `(Option Int)`, `(object IGreeter)`) — the same missing-type-span problem that blocks renaming them. Find-references is still not scope-aware for locals (`ReferencesHandler` goes straight to `SymbolResolver`, so it matches same-file occurrences by bare name), unlike definition, rename, and highlight.

## Tier 2 — meaningful features, moderate effort

- ~~**Code Actions / Quick Fixes**~~ — **done**: "add missing match arms" (ZS0002), "add missing import" (ZS0001), and "prefix with underscore" / "remove unused binding" (ZS0003; the remove fix replaces the form with its body when the bound value is pure, else rewrites to `(begin …)`).
- ~~**Semantic Tokens**~~ — **done** (full + delta + range).
- ~~**Folding Ranges** and **Selection Ranges**~~ — **done** (lexical bracket tree, so both work mid-edit on unbalanced source).
- ~~**Type Definition** and **Implementation**~~ — **done** (implementation uses a new interface→implementors index facet; the class `: Base IFoo` name list is indexed whole since the AST can't split base class from interfaces).
- ~~**CodeLens**~~ — **done** ("N references", clickable via the client-side `editor.action.showReferences` command).

## Tier 3 — weaknesses in existing features

- ~~**Completion is static and un-scoped**~~ — **done**: server-side prefix filtering, cross-file/imported symbols from the workspace index (module shown as detail, sorted after same-file symbols), scope-filtered params/locals, and type-vs-expression context awareness. Still no docs, no snippets, `ResolveProvider = false` (see deferred list above).
- ~~**Diagnostics are bare**~~ — **done**: codes + structured data (ZS0001–ZS0003), tags (`Unnecessary` on ZS0003), and related information ("existing arm here" on ZS0002).
- **Hover has no documentation**: the compiler captures no doc comments (no `;;;` / leading-comment convention exists), so hover is type-only. Deferred deliberately — designing the doc-comment convention is a language-level effort. Note the lexer can now retain comment tokens (`Tokenize(keepComments: true)`), which is the first prerequisite.

## Tier 4 — infrastructure / correctness gaps

- ~~**No file-watching**~~ — **done**: `DidChangeWatchedFilesHandler` watches `**/*.zs` + `**/*.zspkg`; creates/changes queue a coalesced re-index (500 ms quiet period, open buffers win over disk), deletes purge the index, manifest changes re-index the package's files, and `didClose` re-syncs from disk.
- ~~No **work-done progress**~~ — **done** for the startup scan (`IWorkspaceScanReporter` keeps `AnalysisService` LSP-free; `WorkspaceScanProgressReporter` implements it over `IServerWorkDoneManager`).
- ~~**No `didChangeWorkspaceFolders`**~~ — **done** (`WorkspaceFoldersHandler`: added folders get a background scan via `AnalysisService.ScanAdditionalRootsAsync`, removed folders are purged via `PurgeRoot`). Still no **`workspace/executeCommand`** — but nothing needs it anymore: quick fixes use inline `WorkspaceEdit`s and CodeLens uses the client-side command (below).
- ~~**Call Hierarchy** and **Type Hierarchy**~~ — **done**: the compiler records no call graph, so both directions are derived from the index — every `IndexedReference` now carries the qualified key of its enclosing top-level definition (`ContainingDefinition`, tagged by `ReferenceCollector`); incoming calls group a function's references by container, outgoing calls resolve the references it contains (record/union-case constructors count as calls; ambiguous names are skipped, not guessed). Type hierarchy reads the implementations facet non-transitively (one level per expansion); supertypes come from the declaration's own base list. Module-scope calls have no caller item; never-opened files share find-references' staleness limits. ~~**Declaration**~~ — **done** (`DeclarationHandler`; still the same answer as Definition, but the request no longer errors). **Moniker** (LSIF niche) and **Linked Editing** (little value for this syntax) are explicitly won't-do. ~~Document Links~~ — **done** (clickable `import` module names).
- ~~**Formatter**~~ — **done**: `textDocument/formatting` and `textDocument/rangeFormatting` run `ZScheme.Formatter` (the same code path as `zs format`) over the live editor buffer, so editor output and the CLI can't diverge. Notes:
  - The buffer is read from `AnalysisService.GetBufferText`, recorded synchronously on open/change. `DocumentState.Source` is *not* usable here: analysis is debounced 300 ms, so it can trail the buffer, and edits computed from stale text would corrupt the document.
  - Config precedence is defaults < the client's `tabSize`/`insertSpaces` < `.editorconfig` < `.zsfmt`, so an editor's indent setting applies only where the project hasn't pinned one (`Formatter.ResolveOptions`).
  - `FormattingEdits` reduces the reformat to line-granular hunks rather than one whole-buffer replace (which costs folds/scroll position in some clients). **Range formatting is the same full-document diff with non-overlapping hunks dropped** — so a selection can never be laid out differently from a full format, and no fragment is re-parsed in isolation. Line-for-line hunks are split per line first so a selection is honoured to the line.
  - When the formatter declines (lex/parse errors, or its re-lex token-stream guard tripping) the response is simply no edits — formatting a file that is mid-edit is routine and shouldn't raise an error.
  - `.zspkg` manifests are excluded (different grammar), and **on-type formatting is deliberately not implemented**: the formatter is whole-form rather than incremental, so it would reflow text the user is still typing.

## Recommended order

1. ~~**Rename + Document Highlight + Inlay Hints + Signature Help**~~ — **done**.
2. ~~**Fix file-watching staleness**~~ — **done**.
3. ~~**Broaden completion**~~ — **done**.
4. ~~**Code actions**~~ — **done**.
5. ~~**Semantic tokens, folding/selection ranges, type definition/implementation, document links, codelens, workspace-scan progress, unused-binding analysis (ZS0003 + `Unnecessary` tag + quick fixes), diagnostic related-info, ZS0002 → Error**~~ — **done**.

6. ~~**Scope-aware rename/highlight/completion + context-aware completion**~~ — **done** (`ScopeAnalysis` + `TypePosition`; `Param.NameSpan` added compiler-side).
7. ~~**Rename from type-declaration names**~~ — **done** (`NameSpan` on `RecordDecl`/`UnionDecl`/`ClassDecl`/`InterfaceDecl`/`UnionCase`).
8. ~~**Call-site parameter-name inlay hints + named signature labels**~~ — **done** (`ParamNames` index facet + `ParamNameResolver`).
9. ~~**Call hierarchy + type hierarchy**~~ — **done** (`ContainingDefinition` on references; `CallHierarchyHandler` / `TypeHierarchyHandler`).
10. ~~**Unused parameters + unused private top-level defines (ZS0003) with a `warn-unused-params` toggle, and the `let*` remove-binding fix**~~ — **done** (multi-binding `let`/`let*` pairs are deleted when the value is pure; impure values in a chain keep only the underscore fix).
11. ~~**Clickable CodeLens, semantic-token delta/range, `didChangeWorkspaceFolders`**~~ — **done**.
12. ~~**Document + range formatting**~~ — **done** (`ZScheme.Formatter` over the live buffer, minimal line edits).

Next candidates: hover documentation (needs a doc-comment convention — language design first), rename covering type-annotation positions (needs type-position spans), "remove unused parameter" quick fix.
