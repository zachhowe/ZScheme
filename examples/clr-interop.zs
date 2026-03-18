;; CLR interop syntax demonstration
;;
;; import-clr binds .NET static methods to local names.
;; Note: import-clr parses and type-checks but does not
;; wire up runtime dispatch in the current compiler.

(import-clr
  [sqrt System.Math/Sqrt]
  [writeln System.Console/WriteLine])

;; After import-clr, these names would be callable:
;;   (sqrt 16)        => 4.0
;;   (writeln "hello") => prints "hello"
