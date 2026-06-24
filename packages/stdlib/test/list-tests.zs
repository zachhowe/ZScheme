;; list-tests.zs — Tests for List (singly linked list)
(namespace ZScheme.StdLib.Tests)
(module list-tests)

(import zunit)
(import stdlib/list)
(import stdlib/treelist)
(import stdlib/vector)
(import stdlib/mutable/vector)
(import stdlib/mutable/treelist)

(test-suite ListTests
  (test-case empty_is_empty
    (check-true (empty? Nil)))

  (test-case cons_is_not_empty
    (check-false (empty? (Cons 1 Nil))))

  (test-case empty_returns_nil
    (check-true (empty? (list/empty))))

  (test-case head_returns_first
    (check-equal? 1 (list-head (Cons 1 (Cons 2 Nil)))))

  (test-case tail_returns_rest
    (check-equal? 2 (list-head (list-tail (Cons 1 (Cons 2 Nil))))))

  (test-case rest_returns_rest
    (check-equal? 2 (list-head (rest (Cons 1 (Cons 2 Nil))))))

  (test-case rest_of_empty_is_empty
    (check-true (empty? (rest Nil))))

  (test-case cons_prepends
    (check-equal? 0 (list-head (cons 0 (Cons 1 Nil)))))

  (test-case car_returns_first
    (check-equal? 1 (car (cons 1 (cons 2 Nil)))))

  (test-case cdr_returns_rest
    (check-equal? 2 (car (cdr (cons 1 (cons 2 Nil))))))

  (test-case cons_builds_list
    (check-equal? 3 (length (cons 1 (cons 2 (cons 3 Nil))))))

  ;; List uses cons/car/cdr; TreeList uses treelist-cons/treelist-first/treelist-rest.
  ;; The two APIs coexist with no name collision.

  (test-case treelist_first_returns_head
    (check-equal? 7 (treelist-first (treelist-cons 7 (treelist)))))

  (test-case treelist_rest_returns_tail
    (check-equal? 8 (treelist-first (treelist-rest (treelist-cons 7 (treelist-cons 8 (treelist)))))))

  (test-case car_returns_first_on_list
    (check-equal? 9 (car (cons 9 Nil))))

  (test-case both_apis_in_one_expression
    (let* ([from-list (car (cons 1 Nil))]
           [from-treelist (treelist-first (treelist-cons 2 (treelist)))])
      (check-equal? 3 (+ from-list from-treelist))))

  (test-case length_empty
    (check-equal? 0 (length Nil)))

  (test-case length_nonempty
    (check-equal? 3 (length (Cons 1 (Cons 2 (Cons 3 Nil))))))

  (test-case nth_returns_element
    (check-equal? 20 (list-ref (Cons 10 (Cons 20 (Cons 30 Nil))) 1)))

  (test-case nth_returns_first
    (check-equal? 10 (list-ref (Cons 10 (Cons 20 Nil)) 0)))

  (test-case reverse_empty
    (check-true (empty? (reverse Nil))))

  (test-case reverse_nonempty
    (check-equal? 3 (list-head (reverse (Cons 1 (Cons 2 (Cons 3 Nil)))))))

  (test-case reverse_preserves_length
    (check-equal? 3 (length (reverse (Cons 1 (Cons 2 (Cons 3 Nil)))))))

  (test-case map_transforms_elements
    (let ([result (map (Cons 1 (Cons 2 (Cons 3 Nil))) (lambda (x) (* x 2)))])
      (check-equal? 3 (length result))
      (check-equal? 2 (list-ref result 0))
      (check-equal? 4 (list-ref result 1))
      (check-equal? 6 (list-ref result 2))))

  (test-case map_empty
    (check-true (empty? (map Nil (lambda (x) (* x 2))))))

  (test-case filter_selects_matching
    (let ([result (filter (Cons 1 (Cons 2 (Cons 3 (Cons 4 (Cons 5 Nil))))) (lambda (x) (> x 3)))])
      (check-equal? 2 (length result))
      (check-equal? 4 (list-ref result 0))
      (check-equal? 5 (list-ref result 1))))

  (test-case filter_empty
    (check-true (empty? (filter Nil (lambda (x) (> x 0))))))

  (test-case fold_accumulates
    (check-equal? 15 (fold (Cons 1 (Cons 2 (Cons 3 (Cons 4 (Cons 5 Nil))))) 0 (lambda (acc x) (+ acc x)))))

  (test-case fold_empty
    (check-equal? 0 (fold Nil 0 (lambda (acc x) (+ acc x)))))

  (test-case append_adds_to_end
    (let ([result (append (Cons 1 (Cons 2 (Cons 3 Nil))) 4)])
      (check-equal? 4 (length result))
      (check-equal? 1 (list-head result))
      (check-equal? 4 (list-ref result 3))))

  (test-case concat_joins_lists
    (let ([result (concat (Cons 1 (Cons 2 Nil)) (Cons 3 (Cons 4 (Cons 5 Nil))))])
      (check-equal? 5 (length result))
      (check-equal? 1 (list-head result))
      (check-equal? 5 (list-ref result 4))))

  (test-case concat_empty_left
    (check-equal? 1 (list-head (concat Nil (Cons 1 (Cons 2 Nil))))))

  (test-case concat_empty_right
    (check-equal? 2 (length (concat (Cons 1 (Cons 2 Nil)) Nil))))

  ;; Variadic constructor tests

  (test-case list_constructor_empty
    (check-true (empty? (list))))

  (test-case list_constructor_single
    (let ([xs (list 42)])
      (check-equal? 1 (length xs))
      (check-equal? 42 (list-head xs))))

  (test-case list_constructor_multiple
    (let ([xs (list 10 20 30)])
      (check-equal? 3 (length xs))
      (check-equal? 10 (list-ref xs 0))
      (check-equal? 20 (list-ref xs 1))
      (check-equal? 30 (list-ref xs 2))))

  (test-case list_constructor_with_map
    (let ([result (map (list 1 2 3) (lambda (x) (* x 2)))])
      (check-equal? 3 (length result))
      (check-equal? 2 (list-ref result 0))
      (check-equal? 4 (list-ref result 1))
      (check-equal? 6 (list-ref result 2))))

  (test-case list_constructor_fold
    (check-equal? 15 (fold (list 1 2 3 4 5) 0 (lambda (acc x) (+ acc x)))))

  ;; Conversion: treelist->list

  (test-case treelist_to_list_empty
    (check-true (empty? (treelist->list (treelist)))))

  (test-case treelist_to_list_preserves_elements
    (let ([result (treelist->list (treelist 10 20 30))])
      (check-equal? 3 (length result))
      (check-equal? 10 (list-ref result 0))
      (check-equal? 20 (list-ref result 1))
      (check-equal? 30 (list-ref result 2))))

  ;; Conversion: vector->list

  (test-case vector_to_list_empty
    (check-true (empty? (vector->list (vector)))))

  (test-case vector_to_list_preserves_elements
    (let ([result (vector->list (vector 10 20 30))])
      (check-equal? 3 (length result))
      (check-equal? 10 (list-ref result 0))
      (check-equal? 20 (list-ref result 1))
      (check-equal? 30 (list-ref result 2))))

  ;; Conversion: mutable-vector->list

  (test-case mutable_vector_to_list_empty
    (check-true (empty? (mutable-vector->list (vector->mutable-vector (vector))))))

  (test-case mutable_vector_to_list_preserves_elements
    (let ([result (mutable-vector->list (vector->mutable-vector (vector 10 20 30)))])
      (check-equal? 3 (length result))
      (check-equal? 10 (list-ref result 0))
      (check-equal? 20 (list-ref result 1))
      (check-equal? 30 (list-ref result 2))))

  ;; Conversion: mutable-treelist->list

  (test-case mutable_treelist_to_list_empty
    (check-true (empty? (mutable-treelist->list (treelist-copy (treelist))))))

  (test-case mutable_treelist_to_list_preserves_elements
    (let ([result (mutable-treelist->list (treelist-copy (treelist 10 20 30)))])
      (check-equal? 3 (length result))
      (check-equal? 10 (list-ref result 0))
      (check-equal? 20 (list-ref result 1))
      (check-equal? 30 (list-ref result 2))))

  ;; Conversion: list->treelist

  (test-case list_to_treelist_empty
    (check-true (treelist-empty? (list->treelist (list)))))

  (test-case list_to_treelist_preserves_elements
    (let ([result (list->treelist (list 10 20 30))])
      (check-equal? 3 (treelist-length result))
      (check-equal? 10 (treelist-ref result 0))
      (check-equal? 20 (treelist-ref result 1))
      (check-equal? 30 (treelist-ref result 2))))

  ;; Conversion: list->vector

  (test-case list_to_vector_empty
    (check-true (vector-empty? (list->vector (list)))))

  (test-case list_to_vector_preserves_elements
    (let ([result (list->vector (list 10 20 30))])
      (check-equal? 3 (vector-length result))
      (check-equal? 10 (vector-ref result 0))
      (check-equal? 20 (vector-ref result 1))
      (check-equal? 30 (vector-ref result 2))))

  ;; Conversion: list->mutable-treelist

  (test-case list_to_mutable_treelist_empty
    (check-true (mutable-treelist-empty? (list->mutable-treelist (list)))))

  (test-case list_to_mutable_treelist_preserves_elements
    (let ([result (list->mutable-treelist (list 10 20 30))])
      (check-equal? 3 (mutable-treelist-length result))
      (check-equal? 10 (mutable-treelist-ref result 0))
      (check-equal? 20 (mutable-treelist-ref result 1))
      (check-equal? 30 (mutable-treelist-ref result 2))))

  ;; Conversion: list->mutable-vector

  (test-case list_to_mutable_vector_empty
    (check-true (vector-empty? (list->mutable-vector (list)))))

  (test-case list_to_mutable_vector_preserves_elements
    (let ([result (list->mutable-vector (list 10 20 30))])
      (check-equal? 3 (vector-length result))
      (check-equal? 10 (vector-ref result 0))
      (check-equal? 20 (vector-ref result 1))
      (check-equal? 30 (vector-ref result 2))))

  ;; Round-trip tests

  (test-case round_trip_via_treelist
    (let* ([original (list 1 2 3)]
           [result (treelist->list (list->treelist original))])
      (check-equal? 3 (length result))
      (check-equal? 1 (list-ref result 0))
      (check-equal? 2 (list-ref result 1))
      (check-equal? 3 (list-ref result 2))))

  (test-case round_trip_via_vector
    (let* ([original (list 1 2 3)]
           [result (vector->list (list->vector original))])
      (check-equal? 3 (length result))
      (check-equal? 1 (list-ref result 0))
      (check-equal? 2 (list-ref result 1))
      (check-equal? 3 (list-ref result 2)))))
