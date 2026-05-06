;; slist-tests.zs — Tests for SList (singly linked list)
(namespace ZScheme.StdLib.Tests)
(module slist-tests)

(import zunit)
(import stdlib/slist)
(import stdlib/list)
(import stdlib/array)
(import stdlib/mutable/array)
(import stdlib/mutable/list)

(test-suite SListTests
  (test-case empty_is_empty
    (check-true (empty? SNil)))

  (test-case cons_is_not_empty
    (check-false (empty? (SCons 1 SNil))))

  (test-case empty_returns_snil
    (check-true (empty? (slist/empty))))

  (test-case head_returns_first
    (check-equal? 1 (list-head (SCons 1 (SCons 2 SNil)))))

  (test-case tail_returns_rest
    (check-equal? 2 (list-head (list-tail (SCons 1 (SCons 2 SNil))))))

  (test-case rest_returns_rest
    (check-equal? 2 (list-head (rest (SCons 1 (SCons 2 SNil))))))

  (test-case rest_of_empty_is_empty
    (check-true (empty? (rest SNil))))

  (test-case cons_prepends
    (check-equal? 0 (list-head (cons 0 (SCons 1 SNil)))))

  (test-case car_returns_first
    (check-equal? 1 (car (cons 1 (cons 2 SNil)))))

  (test-case cdr_returns_rest
    (check-equal? 2 (car (cdr (cons 1 (cons 2 SNil))))))

  (test-case cons_builds_list
    (check-equal? 3 (length (cons 1 (cons 2 (cons 3 SNil))))))

  ;; Mixed dispatch: with stdlib/list also in scope, cons/car/cdr are
  ;; overloaded across SList and List. Overload resolution must pick the
  ;; right one from the scrutinee's element-container type.

  (test-case car_dispatches_to_list_when_arg_is_list
    (check-equal? 7 (car (cons 7 (list)))))

  (test-case cdr_dispatches_to_list_when_arg_is_list
    (check-equal? 8 (car (cdr (cons 7 (cons 8 (list)))))))

  (test-case car_dispatches_to_slist_when_arg_is_slist
    (check-equal? 9 (car (cons 9 SNil))))

  (test-case both_overloads_in_one_expression
    (let [from-slist (car (cons 1 SNil))]
      (let [from-list (car (cons 2 (list)))]
        (check-equal? 3 (+ from-slist from-list)))))

  (test-case length_empty
    (check-equal? 0 (length SNil)))

  (test-case length_nonempty
    (check-equal? 3 (length (SCons 1 (SCons 2 (SCons 3 SNil))))))

  (test-case nth_returns_element
    (check-equal? 20 (list-ref (SCons 10 (SCons 20 (SCons 30 SNil))) 1)))

  (test-case nth_returns_first
    (check-equal? 10 (list-ref (SCons 10 (SCons 20 SNil)) 0)))

  (test-case reverse_empty
    (check-true (empty? (reverse SNil))))

  (test-case reverse_nonempty
    (check-equal? 3 (list-head (reverse (SCons 1 (SCons 2 (SCons 3 SNil)))))))

  (test-case reverse_preserves_length
    (check-equal? 3 (length (reverse (SCons 1 (SCons 2 (SCons 3 SNil)))))))

  (test-case map_transforms_elements
    (let [result (map (SCons 1 (SCons 2 (SCons 3 SNil))) (lambda (x) (* x 2)))]
      (begin
        (check-equal? 3 (length result))
        (check-equal? 2 (list-ref result 0))
        (check-equal? 4 (list-ref result 1))
        (check-equal? 6 (list-ref result 2)))))

  (test-case map_empty
    (check-true (empty? (map SNil (lambda (x) (* x 2))))))

  (test-case filter_selects_matching
    (let [result (filter (SCons 1 (SCons 2 (SCons 3 (SCons 4 (SCons 5 SNil))))) (lambda (x) (> x 3)))]
      (begin
        (check-equal? 2 (length result))
        (check-equal? 4 (list-ref result 0))
        (check-equal? 5 (list-ref result 1)))))

  (test-case filter_empty
    (check-true (empty? (filter SNil (lambda (x) (> x 0))))))

  (test-case fold_accumulates
    (check-equal? 15 (fold (SCons 1 (SCons 2 (SCons 3 (SCons 4 (SCons 5 SNil))))) 0 (lambda (acc x) (+ acc x)))))

  (test-case fold_empty
    (check-equal? 0 (fold SNil 0 (lambda (acc x) (+ acc x)))))

  (test-case append_adds_to_end
    (let [result (append (SCons 1 (SCons 2 (SCons 3 SNil))) 4)]
      (begin
        (check-equal? 4 (length result))
        (check-equal? 1 (list-head result))
        (check-equal? 4 (list-ref result 3)))))

  (test-case concat_joins_lists
    (let [result (concat (SCons 1 (SCons 2 SNil)) (SCons 3 (SCons 4 (SCons 5 SNil))))]
      (begin
        (check-equal? 5 (length result))
        (check-equal? 1 (list-head result))
        (check-equal? 5 (list-ref result 4)))))

  (test-case concat_empty_left
    (check-equal? 1 (list-head (concat SNil (SCons 1 (SCons 2 SNil))))))

  (test-case concat_empty_right
    (check-equal? 2 (length (concat (SCons 1 (SCons 2 SNil)) SNil))))

  ;; Variadic constructor tests

  (test-case slist_constructor_empty
    (check-true (empty? (slist))))

  (test-case slist_constructor_single
    (let [xs (slist 42)]
      (begin
        (check-equal? 1 (length xs))
        (check-equal? 42 (list-head xs)))))

  (test-case slist_constructor_multiple
    (let [xs (slist 10 20 30)]
      (begin
        (check-equal? 3 (length xs))
        (check-equal? 10 (list-ref xs 0))
        (check-equal? 20 (list-ref xs 1))
        (check-equal? 30 (list-ref xs 2)))))

  (test-case slist_constructor_with_map
    (let [result (map (slist 1 2 3) (lambda (x) (* x 2)))]
      (begin
        (check-equal? 3 (length result))
        (check-equal? 2 (list-ref result 0))
        (check-equal? 4 (list-ref result 1))
        (check-equal? 6 (list-ref result 2)))))

  (test-case slist_constructor_fold
    (check-equal? 15 (fold (slist 1 2 3 4 5) 0 (lambda (acc x) (+ acc x)))))

  ;; Conversion: list->slist

  (test-case list_to_slist_empty
    (check-true (empty? (list->slist (list)))))

  (test-case list_to_slist_preserves_elements
    (let [result (list->slist (list 10 20 30))]
      (begin
        (check-equal? 3 (length result))
        (check-equal? 10 (list-ref result 0))
        (check-equal? 20 (list-ref result 1))
        (check-equal? 30 (list-ref result 2)))))

  ;; Conversion: array->slist

  (test-case array_to_slist_empty
    (check-true (empty? (array->slist (array)))))

  (test-case array_to_slist_preserves_elements
    (let [result (array->slist (array 10 20 30))]
      (begin
        (check-equal? 3 (length result))
        (check-equal? 10 (list-ref result 0))
        (check-equal? 20 (list-ref result 1))
        (check-equal? 30 (list-ref result 2)))))

  ;; Conversion: mutable-array->slist

  (test-case mutable_array_to_slist_empty
    (check-true (empty? (mutable-array->slist (array->mutable-array (array))))))

  (test-case mutable_array_to_slist_preserves_elements
    (let [result (mutable-array->slist (array->mutable-array (array 10 20 30)))]
      (begin
        (check-equal? 3 (length result))
        (check-equal? 10 (list-ref result 0))
        (check-equal? 20 (list-ref result 1))
        (check-equal? 30 (list-ref result 2)))))

  ;; Conversion: mutable-list->slist

  (test-case mutable_list_to_slist_empty
    (check-true (empty? (mutable-list->slist (list->mutable-list (list))))))

  (test-case mutable_list_to_slist_preserves_elements
    (let [result (mutable-list->slist (list->mutable-list (list 10 20 30)))]
      (begin
        (check-equal? 3 (length result))
        (check-equal? 10 (list-ref result 0))
        (check-equal? 20 (list-ref result 1))
        (check-equal? 30 (list-ref result 2)))))

  ;; Conversion: slist->list

  (test-case slist_to_list_empty
    (check-true (empty? (slist->list (slist)))))

  (test-case slist_to_list_preserves_elements
    (let [result (slist->list (slist 10 20 30))]
      (begin
        (check-equal? 3 (length result))
        (check-equal? 10 (list-ref result 0))
        (check-equal? 20 (list-ref result 1))
        (check-equal? 30 (list-ref result 2)))))

  ;; Conversion: slist->array

  (test-case slist_to_array_empty
    (check-true (array-empty? (slist->array (slist)))))

  (test-case slist_to_array_preserves_elements
    (let [result (slist->array (slist 10 20 30))]
      (begin
        (check-equal? 3 (array-length result))
        (check-equal? 10 (array-ref result 0))
        (check-equal? 20 (array-ref result 1))
        (check-equal? 30 (array-ref result 2)))))

  ;; Conversion: slist->mutable-list

  (test-case slist_to_mutable_list_empty
    (check-true (empty? (slist->mutable-list (slist)))))

  (test-case slist_to_mutable_list_preserves_elements
    (let [result (slist->mutable-list (slist 10 20 30))]
      (begin
        (check-equal? 3 (length result))
        (check-equal? 10 (list-ref result 0))
        (check-equal? 20 (list-ref result 1))
        (check-equal? 30 (list-ref result 2)))))

  ;; Conversion: slist->mutable-array

  (test-case slist_to_mutable_array_empty
    (check-true (array-empty? (slist->mutable-array (slist)))))

  (test-case slist_to_mutable_array_preserves_elements
    (let [result (slist->mutable-array (slist 10 20 30))]
      (begin
        (check-equal? 3 (array-length result))
        (check-equal? 10 (array-ref result 0))
        (check-equal? 20 (array-ref result 1))
        (check-equal? 30 (array-ref result 2)))))

  ;; Round-trip tests

  (test-case round_trip_via_list
    (let [original (slist 1 2 3)]
      (let [result (list->slist (slist->list original))]
        (begin
          (check-equal? 3 (length result))
          (check-equal? 1 (list-ref result 0))
          (check-equal? 2 (list-ref result 1))
          (check-equal? 3 (list-ref result 2))))))

  (test-case round_trip_via_array
    (let [original (slist 1 2 3)]
      (let [result (array->slist (slist->array original))]
        (begin
          (check-equal? 3 (length result))
          (check-equal? 1 (list-ref result 0))
          (check-equal? 2 (list-ref result 1))
          (check-equal? 3 (list-ref result 2)))))))
