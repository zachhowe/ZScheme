;; theory-tests.zs — Demonstrates parameterized tests using theory-case
(module theory-tests)
(import zunit)

;; Parameterized test with multiple data rows
(theory-case addition ([x : Int] [y : Int] [expected : Int])
  (inline-data 1 2 3)
  (inline-data 10 20 30)
  (inline-data -1 1 0)
  (inline-data 0 0 0)
  (check-equal? expected (+ x y)))

;; Single-parameter theory
(theory-case is-positive ([x : Int] [expected : Bool])
  (inline-data 1 #t)
  (inline-data 0 #f)
  (inline-data -5 #f)
  (check-equal? expected (> x 0)))

;; Regular fact test alongside theories
(test-case multiplication
  (check-equal? 42 (* 6 7)))
