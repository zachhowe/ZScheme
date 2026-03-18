;; CLR interop syntax demonstration
;;
;; import-clr binds .NET static methods to local names.

(import-clr
  [writeln System.Console/WriteLine])

(writeln "hello")