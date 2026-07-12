;; CLR object construction with (new ...) special form
;;
;; (new TypeName args...) constructs a .NET object by calling its constructor.

(namespace ZScheme.Examples)

(import-clr
  [writeln System.Console/WriteLine])

(module clr-new)

;; Construct a System.Object (no args)
(let ([_obj (new System.Object)])
  (writeln "constructed object"))

;; Construct a StringBuilder with an initial string
(let ([_sb (new System.Text.StringBuilder "Hello, ZScheme!")])
  (writeln "constructed string builder"))

;; Construct an ArrayList with initial capacity
(let ([_lst (new System.Collections.ArrayList 16)])
  (writeln "constructed array list"))

(define (main [_args : (Mutable-Vector String)]) : Int 0)
