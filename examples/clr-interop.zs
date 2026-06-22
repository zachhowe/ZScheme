(module clr-interop)

;; CLR interop syntax demonstration
;;
;; import-clr binds .NET static methods to local names.
;; namespace sets the C# namespace for the generated output.

(namespace ZScheme.Examples)

(import-clr
  [writeln System.Console/WriteLine])

;; Top-level let with side effects runs in the static initializer
(let ([x "hello"])
  (writeln x))

(define (main [args : (TreeList String)]) : Int 0)