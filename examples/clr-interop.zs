;; CLR interop syntax demonstration
;;
;; import-clr binds .NET static methods to local names.
;; namespace sets the C# namespace for the generated output.

(namespace ZScript.Examples)

(import-clr
  [writeln System.Console/WriteLine])

;; Top-level let with side effects runs in the static initializer
(let [x "hello"]
  (writeln x))

(define (main [args : (List String)]) : Int 0)