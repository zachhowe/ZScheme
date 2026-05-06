;; string.zs — String utilities
(module string)

(import-clr
  [clr-format System.String/Format : (String (Mutable-Array String) -> String)]
  [clr-string-equals System.String/Equals : (String String -> Bool)]
  [clr-string-is-empty System.String/IsNullOrEmpty : (String -> Bool)]
  [clr-starts-with System.String.StartsWith :instance : (String String -> Bool)]
  [clr-ends-with System.String.EndsWith :instance : (String String -> Bool)])

(define (string/format [fmt : String] [args : String ...]) : String
  (clr-format fmt args))

(define (string/equals? [s1 : String] [s2 : String]) : Bool
  (clr-string-equals s1 s2))

(define (string/empty? [s : String]) : Bool
  (clr-string-is-empty s))

(define (string/starts-with? [s : String] [prefix : String]) : Bool
  (clr-starts-with s prefix))

(define (string/ends-with? [s : String] [suffix : String]) : Bool
  (clr-ends-with s suffix))

(export string/format string/equals? string/empty? string/starts-with? string/ends-with?)
