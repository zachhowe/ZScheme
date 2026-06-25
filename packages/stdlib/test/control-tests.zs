;; control-tests.zs — Tests for when/unless macros
(namespace ZScheme.StdLib.Tests)
(module control-tests)

(import zunit)
(import stdlib/control)
(import stdlib/vector)
(import stdlib/mutable/vector)

;; The when/unless bodies are Unit-typed (side-effecting), so we observe whether
;; the body ran by mutating a single-slot mutable vector (sentinel 0 -> 1).

(test-suite ControlTests
  (test-case when_true_runs_body
    (let ([xs (vector->mutable-vector (vector 0))])
      (when #t (vector-set! xs 0 1))
      (check-equal? 1 (vector-ref xs 0))))

  (test-case when_false_skips_body
    (let ([xs (vector->mutable-vector (vector 0))])
      (when #f (vector-set! xs 0 1))
      (check-equal? 0 (vector-ref xs 0))))

  (test-case unless_false_runs_body
    (let ([xs (vector->mutable-vector (vector 0))])
      (unless #f (vector-set! xs 0 1))
      (check-equal? 1 (vector-ref xs 0))))

  (test-case unless_true_skips_body
    (let ([xs (vector->mutable-vector (vector 0))])
      (unless #t (vector-set! xs 0 1))
      (check-equal? 0 (vector-ref xs 0))))

  (test-case when_multiple_body_exprs
    (let ([xs (vector->mutable-vector (vector 0 0))])
      (when #t
        (vector-set! xs 0 1)
        (vector-set! xs 1 2))
      (check-equal? 1 (vector-ref xs 0))
      (check-equal? 2 (vector-ref xs 1))))

  (test-case unless_multiple_body_exprs
    (let ([xs (vector->mutable-vector (vector 0 0))])
      (unless #f
        (vector-set! xs 0 1)
        (vector-set! xs 1 2))
      (check-equal? 1 (vector-ref xs 0))
      (check-equal? 2 (vector-ref xs 1))))

  (test-case when_computed_test
    (let ([xs (vector->mutable-vector (vector 0))])
      (when (> 5 3) (vector-set! xs 0 1))
      (check-equal? 1 (vector-ref xs 0))))

  (test-case unless_computed_test
    (let ([xs (vector->mutable-vector (vector 0))])
      (unless (> 3 5) (vector-set! xs 0 1))
      (check-equal? 1 (vector-ref xs 0)))))
