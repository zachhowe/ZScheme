;; mutable-vector-tests.zs — Tests for Mutable-Vector operations
(namespace ZScheme.StdLib.Tests)
(module mutable-vector-tests)

(import zunit)
(import stdlib/vector)
(import stdlib/mutable/vector)

(test-suite MutableVectorTests
  (test-case count_returns_length
    (check-equal? 3 (vector-length (vector->mutable-vector (vector 1 2 3)))))

  (test-case nth_returns_element
    (check-equal? 20 (vector-ref (vector->mutable-vector (vector 10 20 30)) 1)))

  (test-case set_replaces_element
    (let ([xs (vector->mutable-vector (vector 1 2 3))])
      (vector-set! xs 1 99)
      (check-equal? 99 (vector-ref xs 1))))

  (test-case empty_on_empty_vector
    (check-true (vector-empty? (vector->mutable-vector (vector)))))

  (test-case empty_on_nonempty_vector
    (check-false (vector-empty? (vector->mutable-vector (vector 1)))))

  (test-case map_in_place
    (let ([xs (vector->mutable-vector (vector 1 2 3))])
      (vector-map! xs (lambda (x) (* x 10)))
      (check-equal? 10 (vector-ref xs 0))
      (check-equal? 30 (vector-ref xs 2))))

  (test-case fill_sets_all
    (let ([xs (vector->mutable-vector (vector 1 2 3 4))])
      (vector-fill! xs 7)
      (check-equal? 7 (vector-ref xs 0))
      (check-equal? 7 (vector-ref xs 3))))

  (test-case copy_region
    (let ([src (vector->mutable-vector (vector 10 20 30 40 50))])
      (let ([dst (vector->mutable-vector (vector 0 0 0 0 0))])
        (vector-copy! dst 1 src 2 3)
        (check-equal? 0 (vector-ref dst 0))
        (check-equal? 30 (vector-ref dst 1))
        (check-equal? 40 (vector-ref dst 2))
        (check-equal? 50 (vector-ref dst 3))
        (check-equal? 0 (vector-ref dst 4)))))

  (test-case sort_in_place
    (let ([xs (vector->mutable-vector (vector 3 1 4 1 5 9 2 6))])
      (vector-sort! xs (lambda (a b) (< a b)))
      (check-equal? 1 (vector-ref xs 0))
      (check-equal? 9 (vector-ref xs 7)))))
