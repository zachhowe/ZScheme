# LSP Functionality Gaps

Remaining gaps in `src/ZScheme.LanguageServer/`. The backlog from the original gap
analysis has shipped: scope-aware references, pattern-constructor navigation, type-
annotation navigation and rename, `with-handlers` binder support, completion snippets,
the remove-unused-parameter quick fix, and the formatting handler seam. The plan that
closed them is in `docs/LSP_GAPS_PLAN.md`; tests live in
`tests/ZScheme.LanguageServer.Tests/`. Only the items below remain open.

## Blocked on language design: documentation

- **Hover (and completion) have no documentation.** The compiler captures no doc
  comments — no `;;;` / leading-comment convention exists — so hover is type-only
  markdown and completion items carry no `Documentation` (`ResolveProvider` stays
  `false`; resolve would only add docs, so it stays off until they exist).
  `Tokenize(keepComments: true)` is the prerequisite and already works.

  **Proposed convention (awaiting sign-off — do not implement before then):**
  - *Syntax*: a run of `;;;` line comments immediately preceding a form (no blank line
    between the run and the form) is that form's doc comment; runs accumulate
    top-to-bottom into one string. `;;` stays an ordinary comment. First paragraph =
    summary, remainder free-form Markdown.
  - *Attachment targets*: top-level and member definition forms — `define`,
    `define-value`, `define-record`/`-union`/`-class`/`-interface`, object/class
    methods, module members.
  - *Capture*: no AST record changes — the lexer already produces comment tokens with
    spans. Association is purely positional (comment run's last line == form's first
    line − 1); a `DocCommentExtractor` in the language server maps each definition's
    start line to its doc string on `DocumentState`, mirrored into
    `IndexedDefinition.Documentation` for cross-file consumers.
  - *Surfacing once approved*: hover markdown under the type line; completion
    `Documentation` (flipping `ResolveProvider` to true and serving docs in
    `CompletionHandler.Handle(CompletionItem)`); `SignatureInformation.Documentation`.

## Blocked on the formatter branch

- **Formatting is wired but inert.** `DocumentFormattingHandler`,
  `DocumentRangeFormattingHandler`, and `DocumentOnTypeFormattingHandler` delegate to
  the injected `ISourceFormatter`; the registered `NullFormatter` declines every
  request (null for full/on-type, an empty edit list for range — the OmniSharp range
  base models the result as non-nullable). When the ZScheme formatter lands from its
  branch, it implements `ISourceFormatter` and is swapped in via DI; no handler
  changes are needed. One seam limitation to remember: `ISourceFormatter` carries no
  trigger character or position, so on-type formatting will need the interface to grow
  when the formatter lands.

## Deliberately absent

- **`workspace/executeCommand`.** Nothing needs it: code actions return inline
  `WorkspaceEdit`s and CodeLens uses the client-side `editor.action.showReferences`
  command. Implement only if a feature actually requires a server-side command.
- **Moniker** — LSIF-era niche, no consumer for this language.
- **Linked editing ranges** — little value for S-expression syntax.
