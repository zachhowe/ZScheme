(namespace ZScript.Examples)

(module boolean)

;; Boolean logic and comparisons

;; Both arguments are positive
(define (both-positive [a : Int] [b : Int]) : Bool
  (and (> a 0) (> b 0)))

;; At least one argument is zero
(define (either-zero [a : Int] [b : Int]) : Bool
  (or (= a 0) (= b 0)))

;; Value is not zero
(define (is-nonzero [x : Int]) : Bool
  (not (= x 0)))

;; Value is within an inclusive range
(define (in-range [x : Int] [lo : Int] [hi : Int]) : Bool
  (and (>= x lo) (<= x hi)))

;; Sign of an integer: -1, 0, or 1
(define (sign [x : Int]) : Int
  (if (< x 0) -1 (if (= x 0) 0 1)))

;; Exclusive or
(define (xor [a : Bool] [b : Bool]) : Bool
  (and (or a b) (not (and a b))))
