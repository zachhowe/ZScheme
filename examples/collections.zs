;; Lists, vectors, and maps

;; List literal
(define primes (list 2 3 5 7 11))

;; Vector literal
(define coords (vector 10 20 30))

;; Map literal with string keys
(define scores (map-of ("alice" 95) ("bob" 87) ("carol" 92)))

;; A function that returns a list
(define (first-n-squares [n : Int]) : (List Int)
  (list (* 1 1) (* 2 2) (* 3 3) (* 4 4)))
