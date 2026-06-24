;; vector-tests.zs — Tests for Vector operations
(namespace ZScheme.StdLib.Tests)
(module vector-tests)

(import zunit)
(import stdlib/vector)

(test-suite VectorTests
  (test-case count_returns_length
    (check-equal? 3 (vector-length (vector 1 2 3))))

  (test-case nth_returns_element
    (check-equal? 20 (vector-ref (vector 10 20 30) 1)))

  (test-case append_concats_vectors
    (check-equal? 5 (vector-length (vector-append (vector 1 2 3) (vector 4 5)))))

  (test-case append_empty_is_identity
    (check-equal? 3 (vector-length (vector-append (vector 1 2 3)))))

  (test-case append_zero_args_is_empty
    (check-true (vector-empty? (vector-append))))

  (test-case make_vector_fills_with_value
    (let ([v (make-vector 4 7)])
      (check-equal? 4 (vector-length v))
      (check-equal? 7 (vector-ref v 0))
      (check-equal? 7 (vector-ref v 3))))

  (test-case build_vector_uses_index
    (let ([v (build-vector 5 (lambda (i) (* i i)))])
      (check-equal? 5 (vector-length v))
      (check-equal? 0 (vector-ref v 0))
      (check-equal? 1 (vector-ref v 1))
      (check-equal? 16 (vector-ref v 4))))

  (test-case take_returns_prefix
    (let ([v (vector-take (vector 10 20 30 40 50) 3)])
      (check-equal? 3 (vector-length v))
      (check-equal? 10 (vector-ref v 0))
      (check-equal? 30 (vector-ref v 2))))

  (test-case drop_skips_prefix
    (let ([v (vector-drop (vector 10 20 30 40 50) 2)])
      (check-equal? 3 (vector-length v))
      (check-equal? 30 (vector-ref v 0))
      (check-equal? 50 (vector-ref v 2))))

  (test-case copy_slice
    (let ([v (vector-copy (vector 10 20 30 40 50) 1 4)])
      (check-equal? 3 (vector-length v))
      (check-equal? 20 (vector-ref v 0))
      (check-equal? 40 (vector-ref v 2))))

  (test-case sort_orders_ascending
    (let ([v (vector-sort (vector 3 1 4 1 5 9 2 6) (lambda (a b) (< a b)))])
      (check-equal? 1 (vector-ref v 0))
      (check-equal? 9 (vector-ref v 7))))

  (test-case member_returns_index
    (match (vector-member (vector 10 20 30 40) 30)
      [(Some i) (check-equal? 2 i)]
      [None (check-true #f)]))

  (test-case member_returns_none
    (match (vector-member (vector 10 20 30 40) 99)
      [(Some _) (check-true #f)]
      [None (check-true #t)]))

  (test-case count_matching
    (check-equal? 3 (vector-count (vector 1 2 3 4 5) (lambda (x) (> x 2)))))

  (test-case argmin_smallest
    (check-equal? 1 (vector-argmin (vector 5 3 1 4 2) (lambda (x) x))))

  (test-case argmax_largest
    (check-equal? 5 (vector-argmax (vector 5 3 1 4 2) (lambda (x) x))))

  (test-case filter_not_inverts
    (let ([v (vector-filter-not (vector 1 2 3 4 5) (lambda (x) (< x 3)))])
      (check-equal? 3 (vector-length v))
      (check-equal? 3 (vector-ref v 0))))

  (test-case set_copy_replaces_element
    (check-equal? 99 (vector-ref (vector-set/copy (vector 1 2 3) 1 99) 1)))

  (test-case empty_on_empty_vector
    (check-true (vector-empty? (vector))))

  (test-case empty_on_nonempty_vector
    (check-false (vector-empty? (vector 1))))

  (test-case map_transforms_elements
    (let ([result (vector-map (vector 1 2 3) (lambda (x) (* x 10)))])
      (check-equal? 3 (vector-length result))
      (check-equal? 10 (vector-ref result 0))
      (check-equal? 20 (vector-ref result 1))
      (check-equal? 30 (vector-ref result 2))))

  (test-case filter_selects_matching
    (let ([result (vector-filter (vector 1 2 3 4 5) (lambda (x) (< x 4)))])
      (check-equal? 3 (vector-length result))
      (check-equal? 1 (vector-ref result 0))))

  (test-case foldl_accumulates
    (check-equal? 6 (vector-foldl (vector 1 2 3) 0 (lambda (acc x) (+ acc x))))))
