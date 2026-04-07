;; Tuple types backed by .NET ValueTuple

(namespace ZScheme.Examples)

(module tuples)

;; Basic tuple construction
(define pair (values 1 "hello"))

;; Tuple with type annotation
(define (make-pair [x : Int] [y : String]) : (Int * String)
  (values x y))

;; Accessor functions (zero-indexed)
(define (first-of-pair [t : (Int * String)]) : Int
  (value/0 t))

(define (second-of-pair [t : (Int * String)]) : String
  (value/1 t))

;; Tuple pattern matching
(define (swap [t : (Int * String)]) : (String * Int)
  (match t
    [(values x y) (values y x)]))

;; Three-element tuple
(define (make-triple [a : Int] [b : String] [c : Bool]) : (Int * String * Bool)
  (values a b c))

(define (triple-first [t : (Int * String * Bool)]) : Int
  (value/0 t))

;; Nested tuples
(define (nest [a : Int] [b : Int] [c : Int] [d : Int]) : ((Int * Int) * (Int * Int))
  (values (values a b) (values c d)))
