(import zunit)

(test-suite MathTestCases
  (test-case addition (check-equal? (+ 1 2) 3))
  (test-case subtraction (check-equal? (- 3 1) 2)))
