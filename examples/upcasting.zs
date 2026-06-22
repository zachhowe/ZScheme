;; Upcasting with type-annotated let bindings
;;
;; (let ([name : Type expr]) body) binds `name` with the annotated type,
;; which must be a supertype of the inferred expression type.
;; This enables explicit upcasting for CLR interop scenarios.

(namespace ZScheme.Examples)

(import-clr
  [writeln System.Console/WriteLine])

(module upcasting)

;; Upcast MemoryStream to Stream
(let ([s : System.IO.Stream (new System.IO.MemoryStream)])
  (writeln "upcast MemoryStream to Stream"))

;; Upcast to a nullable type
(let ([x : Int? 42])
  (writeln "upcast Int to Int?"))

;; Upcast to System.Object (boxing)
(let ([obj : System.Object 42])
  (writeln "boxed Int to Object"))

(define (main [args : (TreeList String)]) : Int 0)
