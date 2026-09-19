; ZScheme package manifest (*.zspkg) highlighting queries for Zed (Tree-sitter)
; Pattern ordering: LAST match wins — general patterns first, specific overrides last
; Note: #match?/#eq? predicates are evaluated by Zed, not by tree-sitter-cli
;
; Vocabulary mirrors src/ZScheme.Compiler/Package/ManifestParser.cs. This shares the
; generic S-expression grammar with .zs but deliberately none of its vocabulary —
; `define` and `lambda` are not keywords in a manifest.

; === Catch-all: all symbols default to variable ===

(symbol) @variable

; === Literals ===

(comment) @comment

(string) @string
(escape_sequence) @string.escape

(number) @number
(float) @number

; === Punctuation ===

(colon) @punctuation.delimiter

(list "(" @punctuation.bracket)
(list ")" @punctuation.bracket)
(bracket_list "[" @punctuation.bracket)
(bracket_list "]" @punctuation.bracket)

; === Dependency entries: [System.Collections.Immutable "9.0.0"] ===
; bracket_list carries no `head` field, so anchor on the first named child.

(bracket_list . (symbol) @variable.special)

; === :git / :local ===
; The grammar's clr_qualifier token covers only instance/where, so a dependency
; source marker arrives as a colon plus the symbol immediately after it.

(bracket_list (colon) . (symbol) @keyword
  (#match? @keyword "^(git|local)$"))

; === (framework Microsoft.AspNetCore.App) — entries are bare symbols, not strings ===

(list head: (symbol) @_framework
  (#eq? @_framework "framework")
  (symbol) @variable.special)

; === Scalar fields ===

(list head: (symbol) @property
  (#match? @property "^(name|version|entry|import-prefix|default-module|description|license|output|output-type|backend|namespace|ref|sdk|warn-unused-params|stdlib)$"))

; === Sections and the root form (highest priority — last) ===

(list head: (symbol) @keyword
  (#match? @keyword "^(package|dependencies|test-dependencies|build|sources|nuget|zscheme|framework|main|test)$"))
