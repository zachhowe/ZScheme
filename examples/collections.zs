(namespace ZScheme.Examples)

(module collections)

(import stdlib/treelist)
(import stdlib/vector)
(import stdlib/hash)
(import stdlib/option)

;; Lists, vectors, and hashes

;; TreeList literal
(define primes (treelist 2 3 5 7 11))

;; Vector literal
(define coords (vector 10 20 30))

;; Hash literal with string keys
(define scores (hash (pair "alice" 95) (pair "bob" 87) (pair "carol" 92)))

;; A function that returns a treelist
(define (first-n-squares [n : Int]) : (TreeList Int)
  (treelist (* 1 1) (* 2 2) (* 3 3) (* 4 4)))

;; List operations: map, filter, fold
(define doubled-primes (treelist-map primes (lambda (x) (* x 2))))
(define big-primes (treelist-filter primes (lambda (x) (> x 5))))
(define prime-sum (treelist-fold primes 0 (lambda (acc x) (+ acc x))))

;; Vector operations
(define vec-sum (vector-foldl coords 0 (lambda (acc x) (+ acc x))))

;; Hash operations
(define (lookup-score [name : String]) : (Option Int)
  (hash-ref scores name))
