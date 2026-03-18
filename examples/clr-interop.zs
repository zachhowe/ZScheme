;; CLR interop syntax demonstration
;;
;; import-clr binds .NET static methods to local names.

(import-clr
  [writeln System.Console/WriteLine])

(let [x "hello"]
  (writeln x))