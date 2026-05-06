(namespace ZScheme.Examples)

(module collections)

(import stdlib/list)
(import stdlib/array)
(import stdlib/map)
(import stdlib/option)

;; Lists, arrays, and maps

;; List literal
(define primes (list 2 3 5 7 11))

;; Array literal
(define coords (array 10 20 30))

;; Map literal with string keys
(define scores (map-of (pair "alice" 95) (pair "bob" 87) (pair "carol" 92)))

;; A function that returns a list
(define (first-n-squares [n : Int]) : (List Int)
  (list (* 1 1) (* 2 2) (* 3 3) (* 4 4)))

;; List operations: map, filter, fold
(define doubled-primes (list/map primes (lambda (x) (* x 2))))
(define big-primes (list/filter primes (lambda (x) (> x 5))))
(define prime-sum (list/fold primes 0 (lambda (acc x) (+ acc x))))

;; Array operations
(define arr-sum (array/fold coords 0 (lambda (acc x) (+ acc x))))

;; Map operations
(define (lookup-score [name : String]) : (Option Int)
  (map/get scores name))
