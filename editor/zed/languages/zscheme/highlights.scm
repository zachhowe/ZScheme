; ZScheme syntax highlighting queries for Zed (Tree-sitter)
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
(flag_keyword) @keyword
(clr_qualifier) @keyword
(type_variable) @type

; === Non-head symbols: override variable with more specific categories ===

; User-defined types (capitalized identifiers)
(symbol) @type
  (#match? @type "^[A-Z]")

; Built-in type names (override generic type).
; PrimitiveTypeNames.cs plus the stdlib generics. There is no 'Map' —
; packages/stdlib/src/map.zs is hash.zs and the type is Hash.
(symbol) @type.builtin
  (#match? @type.builtin "^(Int|Long|Float|Double|Byte|Char|Bool|String|Unit|Symbol|List|TreeList|Mutable-TreeList|Vector|Hash|Option|Result|Fn|Task)$")

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

; Attribute marker
(list head: (symbol) @attribute
  (#eq? @attribute "@"))

; Module keywords
(list head: (symbol) @keyword
  (#match? @keyword "^(namespace|module|import|export|import-clr)$"))

; Control flow keywords — the AstBuilder special-form switch, plus the macro
; layer's define-syntax/syntax-rules and the type-expression keyword 'delegate'.
; 'and'/'or'/'not' are deliberately absent: they are stdlib macros, not special
; forms, and belong with ordinary function calls.
(list head: (symbol) @keyword
  (#match? @keyword "^(define-syntax|define-async|define-type-alias|define-record|define-struct|define-union|define-class|define-interface|define|letrec|let\\*|let|use\\*|use|if|lambda|match|with-handlers|with|set!|begin|new|raise|await|syntax-rules|typeof|delegate|values|quote)$"))

; Generic type constructors are applied, so they sit in head position —
; (List Int), (Hash String Long) — where the head rules above would otherwise
; claim them as function calls.
(list head: (symbol) @type.builtin
  (#match? @type.builtin "^(List|TreeList|Mutable-TreeList|Vector|Hash|Option|Result|Fn|Task)$"))

; (super/MethodName arg ...) — the lexer reads 'super/Speak' as one symbol, so
; the prefix cannot be captured apart from the method name.
(list head: (symbol) @function
  (#match? @function "^super/"))
