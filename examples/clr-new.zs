;; CLR object construction with (new ...) special form
;;
;; (new TypeName args...) constructs a .NET object by calling its constructor.

(namespace ZScheme.Examples)

(import-clr
  [writeln System.Console/WriteLine])

(module clr-new)

;; Construct a System.Object (no args)
(let [obj (new System.Object)]
  (writeln "constructed object"))

;; Construct a StringBuilder with an initial string
(let [sb (new System.Text.StringBuilder "Hello, ZScheme!")]
  (writeln "constructed string builder"))

;; Construct an ArrayList with initial capacity
(let [lst (new System.Collections.ArrayList 16)]
  (writeln "constructed array list"))

(define (main [args : (List String)]) : Int 0)
