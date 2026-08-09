;; string.zs — String utilities
(module string)

(import stdlib/mutable/vector)

(import-clr
  System
  [clr-format String/Format : (String (Mutable-Vector String) -> String)]
  [clr-string-equals String/Equals : (String String -> Bool)]
  [clr-string-is-empty String/IsNullOrEmpty : (String -> Bool)]
  [clr-starts-with String.StartsWith :instance : (String String -> Bool)]
  [clr-ends-with String.EndsWith :instance : (String String -> Bool)]
  [clr-contains String/Contains :instance : (String String -> Bool)])

(define (format [fmt : String] [args : String ...]) : String
  (clr-format fmt args))

(define (equals? [s1 : String] [s2 : String]) : Bool
  (clr-string-equals s1 s2))

(define (empty? [s : String]) : Bool
  (clr-string-is-empty s))

(define (starts-with? [s : String] [prefix : String]) : Bool
  (clr-starts-with s prefix))

(define (ends-with? [s : String] [suffix : String]) : Bool
  (clr-ends-with s suffix))

(define (contains? [s : String] [substring : String]) : Bool
  (clr-contains s substring))

(export format equals? empty? starts-with? ends-with? contains?)
