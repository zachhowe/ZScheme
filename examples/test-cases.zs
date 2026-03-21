(import zunit)

(test-suite math
  (test-case addition (check-equal-int? (+ 1 2) 3))
  (test-case subtraction (check-equal-int? (- 3 1) 2)))
