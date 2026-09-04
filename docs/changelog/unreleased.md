# 0.5.0 (unreleased)

In development since 2026-08-13.

## Changed — editor support

- **Each ZScheme file extension now has its own syntax definition.** `.zs`, `*.zspkg` and
  `.zsfmt` are three different languages that merely share a lexer, but every editor
  integration had been treating `.zs` and `.zspkg` as one language and `.zsfmt` as nothing at
  all. Each now gets its own language id, scope and grammar in Visual Studio Code, Sublime
  Text and Zed:

  | Extension | Language id | Scope |
  | --- | --- | --- |
  | `.zs` | `zscheme` | `source.zscheme` |
  | `*.zspkg` | `zscheme-package` | `source.zspkg` |
  | `.zsfmt` | `zscheme-fmt` | `source.zsfmt` |

  The manifest and formatter-config grammars are standalone rather than layered over the
  `.zs` one, so `define` and `lambda` no longer highlight as keywords inside a manifest, and
  each highlights its own real vocabulary — `import-prefix`, `sources`, `:local`, `framework`
  entries and dependency names for manifests; settings, both boolean spellings, the
  `space`/`tab` enum and the `-name` removal marker for `.zsfmt`. Unrecognised keys are left
  unhighlighted rather than dressed up as keywords.

  `.zspkg` keeps full language-server support — the client document selectors were widened to
  match, since the server dispatches manifests on the file suffix. `.zsfmt` is deliberately
  not attached to the server, which has no handler for it.

- **Manifest keywords no longer leak into `.zs` files.** `name`, `version`, `build`, `test`,
  `main`, `ref`, `output` and friends were highlighted as keywords in ordinary source; they
  now render as the ordinary function calls they are. Zed was the worst affected, keywordising
  the very common identifiers `main` and `test`.

- **The three `.zs` grammars were reconciled against the compiler**, having drifted apart:
  `use`, `use*`, `quote` and `super/` were missing everywhere; the `import-clr` qualifiers
  `:instance-property-set`, `:instance-property-init`, `:instance-indexer-set` and `:from`
  were unknown to all three; Sublime lacked `null` and the `#:open`/`#:mutable`/`#:init`
  flags; and Zed listed `and`/`or`/`not` as special forms when they are stdlib macros. The
  built-in type list gained `Long`, `Double`, `Byte`, `Char`, `Symbol`, `Hash`, `TreeList` and
  `Mutable-TreeList`, and lost `Map` — a type that does not exist. Generic type constructors
  are written in head position, `(List Int)`, so they are now recognised there instead of
  being claimed as function calls, and user-defined types are scoped apart from built-ins.

- **Zed's tree-sitter grammar mis-lexed the longer `import-clr` qualifiers**:
  `:instance-property-set` matched the `clr_qualifier` token only as far as
  `:instance-property` and left `-set` behind as a separate symbol. The token now matches the
  `-set`/`-init` variants and `:from` whole, with a corpus test pinning it.

## Removed

- **The JetBrains (IntelliJ/Rider) plugin is retired.** It was a JFlex lexer and a hand-written PSI
  layer that had to be kept in step with the compiler by hand, and it lagged. The supported editor
  integrations are now Visual Studio Code, Sublime Text, and Zed — all three of which drive their
  language features from `zs-lsp` rather than a second, divergent implementation of the lexer.
