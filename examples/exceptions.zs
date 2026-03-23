;; exceptions.zs — Demonstrates the (raise ...) special form for throwing CLR exceptions

;; Basic: throw a System.Exception
;; (raise (new System.Exception "something went wrong"))

;; Using raise in a function that validates input
(define (divide [a : Int] [b : Int]) : Int
  (if (= b 0)
    (raise (new System.ArgumentException "divisor cannot be zero"))
    (/ a b)))

;; Combining raise with catch for Result-based error boundaries
(define (safe-divide [a : Int] [b : Int]) : (Result Int ErrorInfo)
  (catch (divide a b)))

;; Using raise in conditional branches — the flexible return type
;; allows raise to unify with Int in the other branch
(define (positive-or-throw [x : Int]) : Int
  (if (> x 0)
    x
    (raise (new System.InvalidOperationException "expected positive number"))))
