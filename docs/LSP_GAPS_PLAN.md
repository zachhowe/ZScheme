# LSP Gaps — Implementation Plan

Dependency-ordered plan to resolve the items in `LSP_GAPS.md`. Ordering: compiler-side
span work first (Steps 1–3, one batch, verified together), then LSP features that consume
it (Steps 4–7), then independent polish (Steps 8–9), then parked infrastructure
(Steps 10–11), then close-out.

Effort sizes: S — Steps 1, 2, 8, 10, 11, 12; M — Steps 3, 4, 5, 6; L — Steps 7, 9.

---

## Phase A — Compiler-side span groundwork

### Step 1. Add a name span to `Pattern.Constructor` — effort: S

1. `src/ZScheme.Compiler/Ast/Pattern.cs` — add trailing optional
   `SourceSpan NameSpan = default` to `Pattern.Constructor` (same convention as
   `UnionCase.NameSpan` / `Param.NameSpan`; add the "NameSpan, when non-empty, points at
   the case-name atom" doc comment).
2. `src/ZScheme.Compiler/Ast/AstBuilder.cs`:
   - `ParseConstructorPattern` (~line 3352): pass the head atom's span
     (`list.Items[0].Span`, already cast to `SExpr.Atom`) as `NameSpan`.
   - `ParsePattern` bare-uppercase-atom case (~line 3316): `NameSpan = a.Span`.
3. `src/ZScheme.Compiler/Types/TypeInferer.cs` — `ResolveBareCasePatterns`
   (~lines 1206–1212) must propagate spans when it rebuilds patterns: the
   variable→constructor rewrite carries `v.Span` as `NameSpan`; the field-rewrite rebuild
   carries `c.NameSpan` (guard: if the source pattern had an empty NameSpan, keep it
   empty — these are synthesized).
4. Verify no other `Pattern.Constructor` construction sites exist (grep confirms only
   these four).
5. Tests (`tests/ZScheme.Compiler.Tests/Ast/` — extend the AstBuilder/pattern tests):
   constructor pattern from `(Circle r)` and bare `Nil` both carry a NameSpan distinct
   from the whole-pattern span.
6. Run: `dotnet build`, `dotnet test --filter "FullyQualifiedName~AstBuilder"`.

### Step 2. Add a binding-var name span to `HandlerClause` — effort: S

1. `src/ZScheme.Compiler/Ast/AstNode.cs` (~line 404) — add trailing
   `SourceSpan BindingNameSpan = default` to `HandlerClause`.
2. `src/ZScheme.Compiler/Ast/AstBuilder.cs` — `BuildWithHandlers` (~line 1502): pass
   `varAtom.Span`.
3. Tests: extend a compiler AstBuilder test to assert the span points at the var atom.
4. Run: narrow filter as in Step 1.

### Step 3. Record type-annotation name uses on `Program` (spans on annotation sites, *not* in `ZType`) — effort: M

1. Decision (least invasive): do **not** add spans to `ZType` records — `ZType`
   structural equality drives unification, and types are constructed throughout
   inference; a span field would poison equality and require touching every construction
   site. Instead record the *written* type-name occurrences per file.
2. New record `TypeNameUse(string Name, SourceSpan Span, int Arity)` (in
   `Ast/AstNode.cs` or a small `Ast/TypeNameUse.cs`).
3. `AstNode.cs` — `AstNode.Program` gains
   `public IReadOnlyList<TypeNameUse> TypeNameUses { get; init; } = [];`.
4. `AstBuilder.cs` — private `List<TypeNameUse> _typeNameUses`, cleared in
   `BuildProgram` (the single entry point), attached to the returned `Program`.
   Collection points, all inside the builder where the `SExpr` atoms and their spans are
   still at hand:
   - `ParseTypeExpr` (~3377): every named-type atom (record name with trailing `?`
     stripped, arity from its type-arg list) — this covers `: Type` annotations, param
     annotations, `new`/`typeof` type args, and nested applications like `(Option Int)`.
   - Base/interface name atoms in `define-class` / `define-interface` / `object` head
     parsing (`ClassDecl.BaseClassName`/`InterfaceNames`, `ObjectExpr`,
     `InterfaceDecl.BaseInterfaceNames`).
   - `with-handlers` exception type atoms (`BuildWithHandlers`).
5. Update `docs/COMPILER-PIPELINE.md` — Stage 3 (AST building): document that written
   type names are recorded on `Program.TypeNameUses` for navigation; note the invariant
   that they mirror the grammar `TypeNameScanner` recognizes.
6. Tests (`tests/ZScheme.Compiler.Tests/Ast/`): occurrences collected for `[x : Point]`,
   return types, `(Option Int)` (both names), class base list, with-handlers clauses;
   empty for expression positions.
7. Phase A verification (per AGENTS.md, compiler changes): `dotnet test` (compiler + LS
   suites), then once for the batch: `pwsh ./run-package-tests.ps1`,
   `pwsh ./run-package-csharp-tests.ps1`, `pwsh ./build-examples.ps1` — AST-additive
   changes with no codegen effect, one run is proportionate.

---

## Phase B — LSP navigation

### Step 4. Make find-references scope-aware (no compiler dependency) — effort: S/M

1. `src/ZScheme.LanguageServer/Handlers/ReferencesHandler.cs` — rework
   `ResolveReferences` to mirror `RenameHandler.ResolveRename` exactly:
   - Locals first: `ScopeAnalysis.LocalOccurrences(state.Ast, line, col)`; map
     occurrences to `Location`s; the binder span counts as the declaration for
     `includeDeclaration` filtering.
   - Top-level targets: after `index.FindReferences(...)`, subtract
     `ScopeAnalysis.OccurrencesBoundLocally(state.Ast, target.BareName)` (same as
     `RenameHandler`/`DocumentHighlightHandler` already do).
2. Tests: new `tests/ZScheme.LanguageServer.Tests/ReferencesTests.cs` — references on a
   local return only its binder + uses; references on a top-level symbol exclude
   same-file occurrences shadow-bound by a local; a same-named top-level symbol is not
   polluted; `includeDeclaration` on/off for both kinds. Existing
   `CrossFileNavigationTests` must stay green.
3. Run: `dotnet test --filter "FullyQualifiedName~ReferencesTests"` +
   `~CrossFileNavigationTests`.

### Step 5. Navigate/index constructor names in match patterns (depends on Step 1) — effort: M

1. `src/ZScheme.LanguageServer/Analysis/AstNavigation.cs` — in `Children`'s
   `AstNode.Match` arm, append synthesized `AstNode.Name` nodes for every
   `Pattern.Constructor` with non-empty `NameSpan` (recursive walk over arm patterns; set
   `ResolvedType` from the pattern's `ResolvedType`). This single change feeds
   `FindNodeAt` (definition/hover), `PathTo`, and `AllNames` (reference index) — which is
   what indexing case-name uses from patterns needs, since `ReferenceCollector` collects
   via `AllNames`.
2. Confirm `ScopeAnalysis` is unaffected: cursor on a pattern-constructor name falls
   through `FindBinder` and resolves via `SymbolResolver` against `NameToDefinition`
   (union cases are in `SymbolCollector`) or the index. Pattern *variables* still resolve
   via the binder `NameSpan` check first.
3. Audit other `AllNames`/`Children` consumers for behavior change:
   `SemanticTokensHandler` (case names in patterns will now tokenize — verify/update
   expectations), `InlayHintHandler`, `SignatureHelpHandler`, `CallHierarchyHandler`
   (pattern case uses gain container attribution — desirable).
4. Tests: extend `DefinitionTests` (go-to-def on `Circle` inside `[(Circle r) ...]` and
   bare `Nil` — currently declining), `RenameTests` (renaming a case rewrites pattern
   uses), `DocumentHighlightTests`, plus a `ReferenceCollector`/`WorkspaceIndexTests`
   case for indexed pattern uses.
5. Run: `dotnet test --filter "FullyQualifiedName~Definition"` + `~SemanticTokens` +
   `~WorkspaceIndex`.

### Step 6. Renameable/highlightable `with-handlers` binding vars (depends on Step 2) — effort: M

1. `src/ZScheme.LanguageServer/Analysis/ScopeAnalysis.cs`:
   - `Collect`: add `case AstNode.WithHandlers wh:` — one binder per handler
     (`BindingVarName`, `BindingNameSpan`, type `null` (exception type is a bare string
     on the AST; leave null, hover unchanged), `SymbolKind.Variable`,
     scope `[h.HandlerBody]`, `FormSpan = h.Span`).
   - `BinderFor`: replace the current `(null, unbindable)` `WithHandlers` arm with a real
     binder when `handler.BindingVarName == name` (unbindable flag only when
     `BindingNameSpan.Length == 0`, consistent with other binders). The shadowing logic
     in `CollectUses` already respects `BindingVarName`.
   - Update the class doc comment's "keep in sync with `UnusedBindingAnalyzer`" note
     (that analyzer already treats handler bindings as shadowing binders).
2. Also covers `DefinitionHandler`/`DeclarationHandler` locals for free (they go through
   `ScopeAnalysis.BindingSiteAt`), and references via Step 4.
3. Tests: extend `ScopeAnalysisTests` (occurrences for handler var: binding + body uses,
   shadowing an outer same-named local), `RenameTests`, `DocumentHighlightTests`.
4. Run: narrow filters for those three test classes.

### Step 7. Type-annotation navigation: definition/references/rename/highlight on type names (depends on Step 3) — effort: L

1. New `src/ZScheme.LanguageServer/Analysis/TypeNavigation.cs`:
   - `TypeNameUse? UseAt(Program, line, col)` — occurrence containing the cursor (spans
     are single-line, disjoint).
   - `ResolvedSymbol? Resolve(...)` — name → same-file `NameToDefinition` restricted to
     type kinds (Record/Union/Class/Interface/TypeAlias, plus the union/record name
     itself), else `index.ResolveDefinition` with a unique-bare-name guard (reuse
     `SymbolResolver.PickBest` semantics; types never carry `ResolvedQualifiedName`).
2. Wire into handlers — each consults `TypeNavigation` after the locals check and
   before/alongside `SymbolResolver` (type uses produce no `Name` node, so there is no
   ambiguity):
   - `DefinitionHandler.ResolveDefinition`, `TypeDefinitionHandler` (annotation use → the
     declared type's definition).
   - `ReferencesHandler.ResolveReferences` and
     `DocumentHighlightHandler.ResolveHighlights`: reference set = definition span +
     every indexed type use matching the target.
   - `RenameHandler.ResolveRename`: edits = definition `NameSpan` + type-use spans across
     files + existing `FindReferences` results (constructor call sites are `Name` nodes
     and are already matched).
3. Index support: `WorkspaceIndex.cs` + `ReferenceCollector.cs` — emit `IndexedReference`
   entries from `Program.TypeNameUses` (container attribution from the enclosing
   top-level form, as for names) and add `bool IsTypeUse = false` to `IndexedReference`;
   add `WorkspaceIndex.FindTypeReferences(string bareName)` (bare-name match on
   `IsTypeUse` entries) so cross-file type matches don't ride the qualified-key path.
   `UpdateFile` plumbing passes the type uses alongside references.
4. Optional hover polish: hover on a type use shows the declaration signature via
   `NameToDefinition.ResolvedType`.
5. Drift guard test: for a corpus file, assert every `TypeNameUse` also appears in
   `TypeNameScanner.Scan` output (the two grammars must stay in step; the ZS0004 analyzer
   keeps working off the token stream unchanged).
6. Tests: new `tests/ZScheme.LanguageServer.Tests/TypeNavigationTests.cs` — definition on
   `[x : Point]`, `: Shape` return type, both names of `(Option Int)`, `: Base IFoo`;
   rename rewrites annotation sites in-file and cross-file; highlight covers declaration
   + annotations; references exclude unrelated same-named locals. Extend
   `RenameTests`/`CrossFileNavigationTests`.
7. Run: `dotnet test --filter "FullyQualifiedName~TypeNavigation"` + `~RenameTests` +
   full LS suite at phase end.

---

## Phase C — Completion & code-action polish (independent)

### Step 8. Completion snippets — effort: S/M

1. `src/ZScheme.LanguageServer/Handlers/CompletionHandler.cs` — upgrade keyword items for
   structural forms to `InsertTextFormat.Snippet` with tab stops, e.g. `define` →
   `(define (${1:name} [${2:arg} : ${3:Int}])\n  $0)`, similarly `let`, `letrec`, `use`,
   `lambda`, `match` (with two arms), `if`, `define-async`, `record`/`union`/`class`/
   `interface`, `with-handlers`. Only when not in a type position (keywords are already
   suppressed there). Leave `ResolveProvider = false` — resolve would only add
   documentation, which is blocked on Step 11.
2. Tests: extend `CompletionTests` — snippet items carry `InsertTextFormat.Snippet`,
   contain tab stops `$0`/`$1`, exact-prefix matching still filters, non-structural
   keywords unchanged.
3. Run: `dotnet test --filter "FullyQualifiedName~CompletionTests"`.

### Step 9. "Remove unused parameter" quick fix for ZS0003 — effort: M/L

1. `src/ZScheme.LanguageServer/Handlers/CodeActionHandler.cs` — in
   `AddUnusedBindingActions`, when the diagnostic's range sits on a parameter name span,
   additionally offer *"Remove parameter 'x' and update call sites"* (keep the existing
   underscore fix first).
2. Param-side edit (token-level, mirroring `BuildRemoveUnusedBindingEdits`' bracket-tree
   approach): find the owning form (`Define`/`DefineAsync`/`Lambda`/`ObjectMethod`/
   `ConstructorDecl`) by walking the AST for the `Param` whose `NameSpan` contains the
   diagnostic range; use `LexicalStructure.BuildTree` to locate the form's param list and
   delete the element (`[x : Int]` bracket or bare atom) plus one adjacent separator.
   Note lambdas' param list is a paren list, defines' params are bracket items — handle
   both shapes.
3. Call-site edits: for the owning function's references (`index.FindReferences`),
   rewrite only *safe* sites:
   - Same-file bare-name matches minus `ScopeAnalysis.OccurrencesBoundLocally`;
     cross-file matches only where the reference carries the matching `QualifiedKey`
     (imported uses), same rule as rename.
   - At each reference span, use the referencing file's token stream (`LexicalStructure`)
     to find the enclosing call bracket and delete the Nth top-level item after the head,
     where N is the removed parameter's index.
   - Decline the whole action (offer only the underscore fix) when any site is unsafe:
     variadic parameter, a `partial` application supplying that argument, argument count
     ≠ parameter count (macro-expanded or unusual forms), or the enclosing bracket can't
     be found mid-edit.
4. Tests: extend `CodeActionTests` — same-file call rewrite (arg removed at every call),
   cross-file rewrite via a package-style qualified reference, underscore fix unchanged,
   declination cases (variadic, partial that supplies the arg, shadowed local call not
   rewritten, arity mismatch).
5. Run: `dotnet test --filter "FullyQualifiedName~CodeActionTests"`.

---

## Phase D — Parked / blocked items

### Step 10. Formatting handlers: thin wiring seam only (real formatter blocked on its branch) — effort: S

1. Explicitly parked: the ZScheme formatter is in progress on another branch; this step
   ships only the seam so the branch can plug in without touching handlers.
2. New `src/ZScheme.LanguageServer/Analysis/ISourceFormatter.cs` —
   `interface ISourceFormatter { IReadOnlyList<TextEdit>? Format(string source,
   OmniSharp Range? range); }` plus a default `NullFormatter` returning null (server
   declines cleanly).
3. New `Handlers/DocumentFormattingHandler.cs`, `DocumentRangeFormattingHandler.cs`,
   `OnTypeFormattingHandler.cs` — derive from the OmniSharp bases, delegate to the
   injected `ISourceFormatter`, fixed document selector like every other handler.
4. `Program.cs` — `.WithHandler<…>` for the three handlers and
   `services.AddSingleton<ISourceFormatter, NullFormatter>()`. Static advertisement comes
   free from `WithHandler` + `StaticCapabilities`.
5. Tests: new `FormattingTests.cs` — null formatter → null result for all three; a fake
   formatter's edits flow through unchanged.
6. Run: `dotnet test --filter "FullyQualifiedName~FormattingTests"`.

### Step 11. Doc comments: convention proposal only — implementation blocked on language-design sign-off — effort: proposal S (implementation M once approved, not planned here)

1. Concrete proposal to take to language design (record in `LSP_GAPS.md` under the gap
   so the decision is traceable):
   - **Syntax**: a run of `;;;` line comments immediately preceding a form (no blank
     line between the run and the form) is that form's doc comment; runs accumulate
     top-to-bottom into one string. `;;` stays an ordinary comment. First paragraph =
     summary, remainder free-form Markdown.
   - **Attachment targets**: top-level and member definition forms — `define`,
     `define`-value, `define-record`/`-union`/`-class`/`-interface`, object/class
     methods, module members.
   - **Capture**: no AST record changes required — the lexer already produces
     `TokenKind.Comment` tokens with spans via `Tokenize(keepComments: true)`.
     Association is purely positional (comment run's last line == form's first line − 1);
     a `DocCommentExtractor` in the language server maps each definition's start line to
     its doc string, stored on `DocumentState` keyed by definition span, and mirrored
     into `IndexedDefinition.Documentation` for cross-file consumers.
   - **Surfacing once approved**: hover markdown appended under the type line;
     completion `Documentation` (+ flip `ResolveProvider` to true and serve docs in
     `CompletionHandler.Handle(CompletionItem)`); `SignatureInformation.Documentation`.
2. Blocked explicitly: all of hover/completion/signature-help documentation waits on
   sign-off of the convention; do not implement before then. Nothing else depends on it.

### Step 12. Close out — effort: S

1. Update `LSP_GAPS.md`: strike the resolved items, record the Step 10 parked status and
   the Step 11 proposal/decision pending.
2. Full verification: `dotnet build`, `pwsh ./run-all-tests.ps1`; no further package runs
   needed beyond Phase A's (no further compiler changes after Step 3).

---

## Files to Modify

- `src/ZScheme.Compiler/Ast/Pattern.cs` — `Pattern.Constructor` gains `NameSpan` (Step 1)
- `src/ZScheme.Compiler/Ast/AstBuilder.cs` — pattern NameSpans (1), handler
  BindingNameSpan (2), `TypeNameUses` collection + `BuildProgram` reset/attach (3)
- `src/ZScheme.Compiler/Types/TypeInferer.cs` — span propagation in
  `ResolveBareCasePatterns` (1)
- `src/ZScheme.Compiler/Ast/AstNode.cs` — `HandlerClause.BindingNameSpan` (2),
  `Program.TypeNameUses` (3)
- `docs/COMPILER-PIPELINE.md` — Stage 3 note on recorded type-name uses (3)
- `src/ZScheme.LanguageServer/Analysis/AstNavigation.cs` — synthesized Name nodes for
  pattern constructors (5)
- `src/ZScheme.LanguageServer/Analysis/ScopeAnalysis.cs` — `WithHandlers` binder cases in
  `Collect`/`BinderFor` (6)
- `src/ZScheme.LanguageServer/Analysis/ReferenceCollector.cs` — index `TypeNameUses` (7)
- `src/ZScheme.LanguageServer/Analysis/WorkspaceIndex.cs` —
  `IndexedReference.IsTypeUse`, `FindTypeReferences` (7)
- `src/ZScheme.LanguageServer/Handlers/ReferencesHandler.cs` — scope-aware resolve (4),
  type-target references (7)
- `src/ZScheme.LanguageServer/Handlers/DefinitionHandler.cs` — type-use resolution (7)
- `src/ZScheme.LanguageServer/Handlers/TypeDefinitionHandler.cs` — annotation-use
  targets (7)
- `src/ZScheme.LanguageServer/Handlers/DocumentHighlightHandler.cs` — type-use highlights
  (7)
- `src/ZScheme.LanguageServer/Handlers/RenameHandler.cs` — type rename (7)
- `src/ZScheme.LanguageServer/Handlers/CompletionHandler.cs` — snippet items (8)
- `src/ZScheme.LanguageServer/Handlers/CodeActionHandler.cs` — remove-unused-parameter
  action (9)
- `src/ZScheme.LanguageServer/Program.cs` — register formatting handlers +
  `ISourceFormatter` (10)
- `LSP_GAPS.md` — status updates (12)
- Tests (extend): `tests/ZScheme.Compiler.Tests/Ast/*` (1–3); `DefinitionTests`,
  `RenameTests`, `DocumentHighlightTests`, `ScopeAnalysisTests`, `SemanticTokensTests`,
  `WorkspaceIndexTests`, `CrossFileNavigationTests`, `CompletionTests`,
  `CodeActionTests` (4–9)

## New Files

- `src/ZScheme.Compiler/Ast/TypeNameUse.cs` (or inline in `AstNode.cs`) —
  `TypeNameUse(Name, Span, Arity)` record (3)
- `src/ZScheme.LanguageServer/Analysis/TypeNavigation.cs` — cursor→type-use→definition
  resolution (7)
- `src/ZScheme.LanguageServer/Analysis/ISourceFormatter.cs` — formatter seam +
  `NullFormatter` (10)
- `src/ZScheme.LanguageServer/Handlers/DocumentFormattingHandler.cs`,
  `DocumentRangeFormattingHandler.cs`, `OnTypeFormattingHandler.cs` (10)
- `tests/ZScheme.LanguageServer.Tests/ReferencesTests.cs` (4),
  `TypeNavigationTests.cs` (7), `FormattingTests.cs` (10)

## Risks

- **Record equality**: adding positional `SourceSpan` fields to
  `Pattern.Constructor`/`HandlerClause` changes structural equality. Patterns are matched
  by `Name` (exhaustiveness) and handler clauses are never compared, but run the full
  compiler suite before proceeding; trailing optional parameters keep all existing
  construction sites source-compatible.
- **Do not put spans in `ZType`**: unification relies on record equality and types are
  rebuilt constantly during inference — the `Program.TypeNameUses` side-list is the
  deliberate least-invasive route (Step 3). State this in the PR description so it isn't
  "cleaned up" later.
- **`AstNavigation.Children` is a shared hub** (Step 5): synthesized pattern-constructor
  `Name` nodes change what `FindNodeAt`/`PathTo`/`AllNames` see — hover, semantic tokens,
  the reference index, call hierarchy and signature help all consume them. Audit each
  consumer's tests; expect and bless new semantic tokens for case names in patterns.
- **Two type-position grammars now exist** (`TypeNameScanner` for ZS0004, `TypeNameUses`
  for navigation): the Step 7.5 consistency test is the drift guard; update both when the
  grammar changes.
- **Cross-file type references have no qualified key** (type uses are never `Name`
  nodes), hence the `IsTypeUse`/`FindTypeReferences` path with a unique-bare-name guard;
  ambiguous type names must decline rather than guess — matches existing
  `SymbolResolver.PickBest` philosophy.
- **Remove-parameter correctness** (Step 9): argument deletion is token-level; any unsafe
  site (variadic, partial, arity mismatch, unbalanced brackets) must make the whole
  action decline — a broken edit is worse than no action. Keep the underscore fix as the
  always-safe fallback.
- **`MatchArm.Pattern` is mutated by `TypeInferer`** — Step 1 must propagate spans
  through those rewrites or navigation breaks only on desugared patterns (hardest kind of
  regression to notice).
- **Formatting** (Step 10): real behavior blocked on the formatter branch; the seam must
  return null (decline), never empty-edit formatting, so clients keep the user's text.
- **Doc comments** (Step 11): blocked on language-design sign-off of the `;;;`
  convention; hover/completion/signature documentation and `ResolveProvider = true` all
  wait — only the proposal text lands now.
- **`ScopeAnalysis`/`UnusedBindingAnalyzer` sync**: Step 6 adds a binder kind; the class
  doc explicitly requires keeping the two walks in sync — update both sides' tests.
