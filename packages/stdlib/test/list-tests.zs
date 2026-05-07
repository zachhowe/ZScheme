;; list-tests.zs — Tests for List (singly linked list)
(namespace ZScheme.StdLib.Tests)
(module list-tests)

(import zunit)
(import stdlib/list)
(import stdlib/treelist)
(import stdlib/array)
(import stdlib/mutable/array)
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

  ;; Mixed dispatch: with stdlib/treelist also in scope, cons/car/cdr are
  ;; overloaded across List and TreeList. Overload resolution must pick the
  ;; right one from the scrutinee's element-container type.

  (test-case car_dispatches_to_treelist_when_arg_is_treelist
    (check-equal? 7 (car (cons 7 (treelist)))))

  (test-case cdr_dispatches_to_treelist_when_arg_is_treelist
    (check-equal? 8 (car (cdr (cons 7 (cons 8 (treelist)))))))

  (test-case car_dispatches_to_list_when_arg_is_list
    (check-equal? 9 (car (cons 9 Nil))))

  (test-case both_overloads_in_one_expression
    (let [from-list (car (cons 1 Nil))]
      (let [from-treelist (car (cons 2 (treelist)))]
        (check-equal? 3 (+ from-list from-treelist)))))

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
    (let [result (map (Cons 1 (Cons 2 (Cons 3 Nil))) (lambda (x) (* x 2)))]
      (begin
        (check-equal? 3 (length result))
        (check-equal? 2 (list-ref result 0))
        (check-equal? 4 (list-ref result 1))
        (check-equal? 6 (list-ref result 2)))))

  (test-case map_empty
    (check-true (empty? (map Nil (lambda (x) (* x 2))))))

  (test-case filter_selects_matching
    (let [result (filter (Cons 1 (Cons 2 (Cons 3 (Cons 4 (Cons 5 Nil))))) (lambda (x) (> x 3)))]
      (begin
        (check-equal? 2 (length result))
        (check-equal? 4 (list-ref result 0))
        (check-equal? 5 (list-ref result 1)))))

  (test-case filter_empty
    (check-true (empty? (filter Nil (lambda (x) (> x 0))))))

  (test-case fold_accumulates
    (check-equal? 15 (fold (Cons 1 (Cons 2 (Cons 3 (Cons 4 (Cons 5 Nil))))) 0 (lambda (acc x) (+ acc x)))))

  (test-case fold_empty
    (check-equal? 0 (fold Nil 0 (lambda (acc x) (+ acc x)))))

  (test-case append_adds_to_end
    (let [result (append (Cons 1 (Cons 2 (Cons 3 Nil))) 4)]
      (begin
        (check-equal? 4 (length result))
        (check-equal? 1 (list-head result))
        (check-equal? 4 (list-ref result 3)))))

  (test-case concat_joins_lists
    (let [result (concat (Cons 1 (Cons 2 Nil)) (Cons 3 (Cons 4 (Cons 5 Nil))))]
      (begin
        (check-equal? 5 (length result))
        (check-equal? 1 (list-head result))
        (check-equal? 5 (list-ref result 4)))))

  (test-case concat_empty_left
    (check-equal? 1 (list-head (concat Nil (Cons 1 (Cons 2 Nil))))))

  (test-case concat_empty_right
    (check-equal? 2 (length (concat (Cons 1 (Cons 2 Nil)) Nil))))

  ;; Variadic constructor tests

  (test-case list_constructor_empty
    (check-true (empty? (list))))

  (test-case list_constructor_single
    (let [xs (list 42)]
      (begin
        (check-equal? 1 (length xs))
        (check-equal? 42 (list-head xs)))))

  (test-case list_constructor_multiple
    (let [xs (list 10 20 30)]
      (begin
        (check-equal? 3 (length xs))
        (check-equal? 10 (list-ref xs 0))
        (check-equal? 20 (list-ref xs 1))
        (check-equal? 30 (list-ref xs 2)))))

  (test-case list_constructor_with_map
    (let [result (map (list 1 2 3) (lambda (x) (* x 2)))]
      (begin
        (check-equal? 3 (length result))
        (check-equal? 2 (list-ref result 0))
        (check-equal? 4 (list-ref result 1))
        (check-equal? 6 (list-ref result 2)))))

  (test-case list_constructor_fold
    (check-equal? 15 (fold (list 1 2 3 4 5) 0 (lambda (acc x) (+ acc x)))))

  ;; Conversion: treelist->list

  (test-case treelist_to_list_empty
    (check-true (empty? (treelist->list (treelist)))))

  (test-case treelist_to_list_preserves_elements
    (let [result (treelist->list (treelist 10 20 30))]
      (begin
        (check-equal? 3 (length result))
        (check-equal? 10 (list-ref result 0))
        (check-equal? 20 (list-ref result 1))
        (check-equal? 30 (list-ref result 2)))))

  ;; Conversion: array->list

  (test-case array_to_list_empty
    (check-true (empty? (array->list (array)))))

  (test-case array_to_list_preserves_elements
    (let [result (array->list (array 10 20 30))]
      (begin
        (check-equal? 3 (length result))
        (check-equal? 10 (list-ref result 0))
        (check-equal? 20 (list-ref result 1))
        (check-equal? 30 (list-ref result 2)))))

  ;; Conversion: mutable-array->list

  (test-case mutable_array_to_list_empty
    (check-true (empty? (mutable-array->list (array->mutable-array (array))))))

  (test-case mutable_array_to_list_preserves_elements
    (let [result (mutable-array->list (array->mutable-array (array 10 20 30)))]
      (begin
        (check-equal? 3 (length result))
        (check-equal? 10 (list-ref result 0))
        (check-equal? 20 (list-ref result 1))
        (check-equal? 30 (list-ref result 2)))))

  ;; Conversion: mutable-treelist->list

  (test-case mutable_treelist_to_list_empty
    (check-true (empty? (mutable-treelist->list (treelist->mutable-treelist (treelist))))))

  (test-case mutable_treelist_to_list_preserves_elements
    (let [result (mutable-treelist->list (treelist->mutable-treelist (treelist 10 20 30)))]
      (begin
        (check-equal? 3 (length result))
        (check-equal? 10 (list-ref result 0))
        (check-equal? 20 (list-ref result 1))
        (check-equal? 30 (list-ref result 2)))))

  ;; Conversion: list->treelist

  (test-case list_to_treelist_empty
    (check-true (empty? (list->treelist (list)))))

  (test-case list_to_treelist_preserves_elements
    (let [result (list->treelist (list 10 20 30))]
      (begin
        (check-equal? 3 (length result))
        (check-equal? 10 (list-ref result 0))
        (check-equal? 20 (list-ref result 1))
        (check-equal? 30 (list-ref result 2)))))

  ;; Conversion: list->array

  (test-case list_to_array_empty
    (check-true (array-empty? (list->array (list)))))

  (test-case list_to_array_preserves_elements
    (let [result (list->array (list 10 20 30))]
      (begin
        (check-equal? 3 (array-length result))
        (check-equal? 10 (array-ref result 0))
        (check-equal? 20 (array-ref result 1))
        (check-equal? 30 (array-ref result 2)))))

  ;; Conversion: list->mutable-treelist

  (test-case list_to_mutable_treelist_empty
    (check-true (empty? (list->mutable-treelist (list)))))

  (test-case list_to_mutable_treelist_preserves_elements
    (let [result (list->mutable-treelist (list 10 20 30))]
      (begin
        (check-equal? 3 (length result))
        (check-equal? 10 (list-ref result 0))
        (check-equal? 20 (list-ref result 1))
        (check-equal? 30 (list-ref result 2)))))

  ;; Conversion: list->mutable-array

  (test-case list_to_mutable_array_empty
    (check-true (array-empty? (list->mutable-array (list)))))

  (test-case list_to_mutable_array_preserves_elements
    (let [result (list->mutable-array (list 10 20 30))]
      (begin
        (check-equal? 3 (array-length result))
        (check-equal? 10 (array-ref result 0))
        (check-equal? 20 (array-ref result 1))
        (check-equal? 30 (array-ref result 2)))))

  ;; Round-trip tests

  (test-case round_trip_via_treelist
    (let [original (list 1 2 3)]
      (let [result (treelist->list (list->treelist original))]
        (begin
          (check-equal? 3 (length result))
          (check-equal? 1 (list-ref result 0))
          (check-equal? 2 (list-ref result 1))
          (check-equal? 3 (list-ref result 2))))))

  (test-case round_trip_via_array
    (let [original (list 1 2 3)]
      (let [result (array->list (list->array original))]
        (begin
          (check-equal? 3 (length result))
          (check-equal? 1 (list-ref result 0))
          (check-equal? 2 (list-ref result 1))
          (check-equal? 3 (list-ref result 2)))))))
