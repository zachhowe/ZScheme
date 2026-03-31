; ZScript syntax highlighting queries for Zed (Tree-sitter)
; Pattern ordering: LAST match wins — general patterns first, specific overrides last
; Note: #match?/#eq? predicates are evaluated by Zed, not by tree-sitter-cli

; === Catch-all: all symbols default to variable ===

(symbol) @variable

; === Literals ===

(comment) @comment

(string) @string
(escape_sequence) @string.escape

(number) @number
(float) @number

(boolean) @constant.builtin

; === Punctuation ===

(colon) @punctuation.delimiter

(list "(" @punctuation.bracket)
(list ")" @punctuation.bracket)
(bracket_list "[" @punctuation.bracket)
(bracket_list "]" @punctuation.bracket)

; === Reader macros ===

(quote "'" @punctuation.special)
(quasiquote "`" @punctuation.special)
(unquote "," @punctuation.special)
(unquote_splicing ",@" @punctuation.special)

; === Special tokens ===

(wildcard) @variable.special
(ellipsis) @operator
(clr_qualifier) @keyword
(type_variable) @type

; === Non-head symbols: override variable with more specific categories ===

; User-defined types (capitalized identifiers)
(symbol) @type
  (#match? @type "^[A-Z]")

; Built-in type names (override generic type)
(symbol) @type.builtin
  (#match? @type.builtin "^(Int|Float|Bool|String|Unit|List|Vector|Map|Option|Result|Fn|Task)$")

; Value constructors (override type)
(symbol) @constructor
  (#match? @constructor "^(Some|None|Ok|Err|Error)$")

; Constraint keywords (override all)
(symbol) @keyword
  (#match? @keyword "^(notnull|struct|unmanaged|default)$")

; === Head position: function calls (lowest head-position priority) ===

(list head: (symbol) @function)

; === Head position: operators (override function) ===

; Arithmetic/comparison operators
(list head: (symbol) @operator
  (#match? @operator "^(<=|>=|!=|[+\\-*/%=<>])$"))

; Pipe operator
(list head: (symbol) @operator
  (#eq? @operator "|>"))

; === Head position: keywords (highest priority, override everything) ===

; Other keywords
(list head: (symbol) @keyword
  (#match? @keyword "^(object|partial)$"))

; Package manifest keywords
(list head: (symbol) @keyword
  (#match? @keyword "^(package|dependencies|nuget|build|output|backend|stdlib|ref|name|version|entry|sources|main|test)$"))

; Attribute marker
(list head: (symbol) @attribute
  (#eq? @attribute "@"))

; Module keywords
(list head: (symbol) @keyword
  (#match? @keyword "^(namespace|module|import|export|import-clr)$"))

; Control flow keywords
(list head: (symbol) @keyword
  (#match? @keyword "^(define-syntax|define-async|define|let\\*|let|if|fn|match|record|union|try|catch|\\?|begin|new|raise|await|class|interface|syntax-rules|and|or|not)$"))
