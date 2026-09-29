# LSP Functionality Gaps

Remaining gaps in `src/ZScheme.LanguageServer/`. Everything previously tracked here as a
planned feature has shipped: all 24 capability handlers are wired in `Program.cs`
(navigation, completion, rename, highlight, inlay hints, signature help, semantic tokens,
folding/selection ranges, type definition, implementation, call/type hierarchy, document
links, CodeLens, code actions, file watching, workspace folders, scan progress), every
capability is advertised statically, and `tests/ZScheme.LanguageServer.Tests/` covers the
handlers. Only the items below remain open.

## Navigation precision

- **Find-references is not scope-aware for locals.** `ReferencesHandler` resolves the
  cursor via `SymbolResolver` and then matches workspace-index references by bare name,
  so a local's occurrences are never scoped to its binder: references on a local either
  return nothing (no same-named top-level symbol) or silently attach to one, and
  references on a top-level symbol include same-file occurrences that actually belong to
  a shadowing local. Definition, rename, and highlight all consult
  `Analysis/ScopeAnalysis.cs` — references is the only navigation feature that doesn't.
  Fix: resolve locals through `ScopeAnalysis` (as `RenameHandler.ResolveRename` does) and
  subtract `OccurrencesBoundLocally` from index results for top-level targets.

- **Go-to-definition declines on constructor names inside match patterns**
  (`(Circle r)`, `Nil`). `AstNode.Match`'s children are the scrutinee plus arm *bodies* —
  `MatchArm.Pattern` is not part of `AstNavigation.Children`, and `Pattern.Constructor`
  carries no name span — so no `Name` node exists at the cursor. Only the pattern's
  *variables* navigate (via `ScopeAnalysis` binder collection). Fix: give
  `Pattern.Constructor` a name span, add patterns to `AstNavigation.Children`, and teach
  `ReferenceCollector` to index case-name uses from patterns.

- **Type annotations are invisible to navigation and rename.** `ZType` records carry no
  `SourceSpan`, so the names in `[x : Point]`, `: Shape` return types, `(Option Int)`,
  and a class's `: Base IFoo` base list resolve to nothing: go-to-definition, rename, and
  document highlight all decline, and renaming a type does not rewrite annotation
  positions. This is why the ZS0004 analyzer works off the token stream
  (`ZScheme.Compiler/Analysis/TypeNameScanner.cs`). Fix: carry spans on type annotations
  (compiler-side), which would simultaneously unlock renaming types.

## Language-design prerequisites

- **Hover (and completion) have no documentation.** The compiler captures no doc
  comments — no `;;;` / leading-comment convention exists — so hover is type-only
  markdown. Deferred deliberately: designing the doc-comment convention is a
  language-level effort. The lexer can already retain comment tokens
  (`Tokenize(keepComments: true)`), which is the first prerequisite; whatever convention
  lands should surface in hover, completion `Documentation`, and signature help.

- **`with-handlers` binding variables are not renameable or highlightable.**
  `HandlerClause` stores only `BindingVarName` (a string) against the whole clause's
  span — no name span — so `ScopeAnalysis` never treats them as binders and rename/highlight
  decline on them. Occurrences still shadow correctly during collection. Fix: add a name
  span to `HandlerClause` (compiler-side) and a binder case in `ScopeAnalysis.Collect`.

## Completion polish

- **No documentation, no snippets, `ResolveProvider = false`.** Items are fully populated
  up front, so resolve would only add documentation — blocked on the doc-comment
  convention above. Snippets (`InsertTextFormat.Snippet` with tab stops) are simply not
  built yet.

## Code actions

- **No "remove unused parameter" quick fix** for ZS0003. Removing a parameter is an
  arity change that needs call-site rewrites, so only the underscore-prefix fix (and the
  remove-binding fix for `let`/`use`) is offered.

## Infrastructure

- **No formatting handlers.** `textDocument/formatting`, range formatting, and on-type
  formatting have no implementation; the ZScheme source formatter is in progress on
  another branch and these will be wired to it when it lands.

- **No `workspace/executeCommand`.** Nothing currently needs it: code actions return
  inline `WorkspaceEdit`s and CodeLens uses the client-side
  `editor.action.showReferences` command, so this stays unimplemented until a feature
  actually requires a server-side command.

## Explicitly won't-do

- **Moniker** — LSIF-era niche, no consumer for this language.
- **Linked editing ranges** — little value for S-expression syntax.
