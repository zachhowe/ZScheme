;; string.zs — String utilities
(module string)

(import-clr
  [clr-format System.String/Format : (Fn [String (Mutable-Array String)] String)]
  [clr-string-equals System.String/Equals : (Fn [String String] Bool)]
  [clr-string-is-empty System.String/IsNullOrEmpty : (Fn [String] Bool)])

(define (string/format [fmt : String] [args : String ...]) : String
  (clr-format fmt args))

(define (string/equals? [s1 : String] [s2 : String]) : Bool
  (clr-string-equals s1 s2))

(define (string/empty? [s : String]) : Bool
  (clr-string-is-empty s))

(export string/format string/equals? string/empty?)
