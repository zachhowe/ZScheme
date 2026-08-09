; ZScheme formatter config (.zsfmt) highlighting queries for Zed (Tree-sitter)
; Pattern ordering: LAST match wins — general patterns first, specific overrides last
; Note: #match?/#eq? predicates are evaluated by Zed, not by tree-sitter-cli
;
; Clause set mirrors src/ZScheme.Formatter/ZsFmtConfig.cs.

; === Catch-all: all symbols default to variable ===

(symbol) @variable

; === Literals ===

(comment) @comment

(string) @string

(number) @number
(float) @number

; === Punctuation ===

(list "(" @punctuation.bracket)
(list ")" @punctuation.bracket)

; === Booleans ===
; #t / #f are grammar-level literals; ZsFmtConfig.ParseBool lowercases before
; matching, so `true`/`false` in any case are booleans here too — even though
; they are ordinary symbols in .zs.

(boolean) @constant

((symbol) @constant
  (#match? @constant "(?i)^(true|false)$"))

; === (indent-style space|tab) ===

((symbol) @constant
  (#match? @constant "(?i)^(space|tab)$"))

; === Members of keep-first-operand / always-break-body ===
; These clauses are deltas over the built-in defaults: a bare name adds, a '-'
; prefix removes. The lexer reads '-if' as one symbol, so the marker cannot be
; captured apart from the name it removes.

(list head: (symbol) @_delta
  (#match? @_delta "^(keep-first-operand|always-break-body)$")
  (symbol) @function)

(list head: (symbol) @_delta
  (#match? @_delta "^(keep-first-operand|always-break-body)$")
  (symbol) @operator
  (#match? @operator "^-"))

; === Clause heads ===

(list head: (symbol) @property
  (#match? @property "^(root|indent-size|indent-style|max-line-length|insert-final-newline|trim-trailing-whitespace|merge-imports|trailing-comment-spaces|keep-first-operand|always-break-body)$"))

; === The root form (highest priority — last) ===

(list head: (symbol) @keyword
  (#eq? @keyword "format"))
