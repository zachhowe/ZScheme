;; cond-tests.zs — Tests for cond macro
(namespace ZScheme.StdLib.Tests)
(module cond-tests)

(import zunit)
(import stdlib/cond)

(test-suite CondTests
  (test-case else_only
    (check-equal? 42 (cond [else 42])))

  (test-case first_clause_matches
    (check-equal? 1 (cond [#t 1] [else 2])))

  (test-case second_clause_matches
    (check-equal? 2 (cond [#f 1] [#t 2] [else 3])))

  (test-case else_fallback
    (check-equal? 3 (cond [#f 1] [#f 2] [else 3])))

  (test-case multiple_body_expressions
    (check-equal? 10 (cond [#t (+ 3 4) (+ 5 5)] [else 0])))

  (test-case with_expressions_in_test
    (check-equal? 1 (cond [(> 5 3) 1] [else 2])))

  (test-case nested_cond
    (check-equal? 3 (cond [#f 1] [else (cond [#f 2] [else 3])]))))
