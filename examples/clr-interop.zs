;; CLR interop syntax demonstration
;;
;; import-clr binds .NET static methods to local names.
;; namespace sets the C# namespace for the generated output.

(namespace MyApp.Demo)

(import-clr
  [writeln System.Console/WriteLine])

(let [x "hello"]
  (writeln x))