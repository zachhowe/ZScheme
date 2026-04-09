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
    (check-true (slist/empty? SNil)))

  (test-case cons_is_not_empty
    (check-false (slist/empty? (SCons 1 SNil))))

  (test-case empty_returns_snil
    (check-true (slist/empty? (slist/empty))))

  (test-case head_returns_first
    (check-equal? 1 (slist/head (SCons 1 (SCons 2 SNil)))))

  (test-case tail_returns_rest
    (check-equal? 2 (slist/head (slist/tail (SCons 1 (SCons 2 SNil))))))

  (test-case rest_returns_rest
    (check-equal? 2 (slist/head (slist/rest (SCons 1 (SCons 2 SNil))))))

  (test-case rest_of_empty_is_empty
    (check-true (slist/empty? (slist/rest SNil))))

  (test-case cons_prepends
    (check-equal? 0 (slist/head (slist/cons 0 (SCons 1 SNil)))))

  (test-case length_empty
    (check-equal? 0 (slist/length SNil)))

  (test-case length_nonempty
    (check-equal? 3 (slist/length (SCons 1 (SCons 2 (SCons 3 SNil))))))

  (test-case nth_returns_element
    (check-equal? 20 (slist/nth (SCons 10 (SCons 20 (SCons 30 SNil))) 1)))

  (test-case nth_returns_first
    (check-equal? 10 (slist/nth (SCons 10 (SCons 20 SNil)) 0)))

  (test-case reverse_empty
    (check-true (slist/empty? (slist/reverse SNil))))

  (test-case reverse_nonempty
    (check-equal? 3 (slist/head (slist/reverse (SCons 1 (SCons 2 (SCons 3 SNil)))))))

  (test-case reverse_preserves_length
    (check-equal? 3 (slist/length (slist/reverse (SCons 1 (SCons 2 (SCons 3 SNil)))))))

  (test-case map_transforms_elements
    (let [result (slist/map (SCons 1 (SCons 2 (SCons 3 SNil))) (fn [x] (* x 2)))]
      (begin
        (check-equal? 3 (slist/length result))
        (check-equal? 2 (slist/nth result 0))
        (check-equal? 4 (slist/nth result 1))
        (check-equal? 6 (slist/nth result 2)))))

  (test-case map_empty
    (check-true (slist/empty? (slist/map SNil (fn [x] (* x 2))))))

  (test-case filter_selects_matching
    (let [result (slist/filter (SCons 1 (SCons 2 (SCons 3 (SCons 4 (SCons 5 SNil))))) (fn [x] (> x 3)))]
      (begin
        (check-equal? 2 (slist/length result))
        (check-equal? 4 (slist/nth result 0))
        (check-equal? 5 (slist/nth result 1)))))

  (test-case filter_empty
    (check-true (slist/empty? (slist/filter SNil (fn [x] (> x 0))))))

  (test-case fold_accumulates
    (check-equal? 15 (slist/fold (SCons 1 (SCons 2 (SCons 3 (SCons 4 (SCons 5 SNil))))) 0 (fn [acc x] (+ acc x)))))

  (test-case fold_empty
    (check-equal? 0 (slist/fold SNil 0 (fn [acc x] (+ acc x)))))

  (test-case append_adds_to_end
    (let [result (slist/append (SCons 1 (SCons 2 (SCons 3 SNil))) 4)]
      (begin
        (check-equal? 4 (slist/length result))
        (check-equal? 1 (slist/head result))
        (check-equal? 4 (slist/nth result 3)))))

  (test-case concat_joins_lists
    (let [result (slist/concat (SCons 1 (SCons 2 SNil)) (SCons 3 (SCons 4 (SCons 5 SNil))))]
      (begin
        (check-equal? 5 (slist/length result))
        (check-equal? 1 (slist/head result))
        (check-equal? 5 (slist/nth result 4)))))

  (test-case concat_empty_left
    (check-equal? 1 (slist/head (slist/concat SNil (SCons 1 (SCons 2 SNil))))))

  (test-case concat_empty_right
    (check-equal? 2 (slist/length (slist/concat (SCons 1 (SCons 2 SNil)) SNil))))

  ;; Variadic constructor tests

  (test-case slist_constructor_empty
    (check-true (slist/empty? (slist))))

  (test-case slist_constructor_single
    (let [xs (slist 42)]
      (begin
        (check-equal? 1 (slist/length xs))
        (check-equal? 42 (slist/head xs)))))

  (test-case slist_constructor_multiple
    (let [xs (slist 10 20 30)]
      (begin
        (check-equal? 3 (slist/length xs))
        (check-equal? 10 (slist/nth xs 0))
        (check-equal? 20 (slist/nth xs 1))
        (check-equal? 30 (slist/nth xs 2)))))

  (test-case slist_constructor_with_map
    (let [result (slist/map (slist 1 2 3) (fn [x] (* x 2)))]
      (begin
        (check-equal? 3 (slist/length result))
        (check-equal? 2 (slist/nth result 0))
        (check-equal? 4 (slist/nth result 1))
        (check-equal? 6 (slist/nth result 2)))))

  (test-case slist_constructor_fold
    (check-equal? 15 (slist/fold (slist 1 2 3 4 5) 0 (fn [acc x] (+ acc x)))))

  ;; Conversion: list->slist

  (test-case list_to_slist_empty
    (check-true (slist/empty? (list->slist (list)))))

  (test-case list_to_slist_preserves_elements
    (let [result (list->slist (list 10 20 30))]
      (begin
        (check-equal? 3 (slist/length result))
        (check-equal? 10 (slist/nth result 0))
        (check-equal? 20 (slist/nth result 1))
        (check-equal? 30 (slist/nth result 2)))))

  ;; Conversion: array->slist

  (test-case array_to_slist_empty
    (check-true (slist/empty? (array->slist (array)))))

  (test-case array_to_slist_preserves_elements
    (let [result (array->slist (array 10 20 30))]
      (begin
        (check-equal? 3 (slist/length result))
        (check-equal? 10 (slist/nth result 0))
        (check-equal? 20 (slist/nth result 1))
        (check-equal? 30 (slist/nth result 2)))))

  ;; Conversion: mutable-array->slist

  (test-case mutable_array_to_slist_empty
    (check-true (slist/empty? (mutable-array->slist (array->mutable-array (array))))))

  (test-case mutable_array_to_slist_preserves_elements
    (let [result (mutable-array->slist (array->mutable-array (array 10 20 30)))]
      (begin
        (check-equal? 3 (slist/length result))
        (check-equal? 10 (slist/nth result 0))
        (check-equal? 20 (slist/nth result 1))
        (check-equal? 30 (slist/nth result 2)))))

  ;; Conversion: mutable-list->slist

  (test-case mutable_list_to_slist_empty
    (check-true (slist/empty? (mutable-list->slist (list->mutable-list (list))))))

  (test-case mutable_list_to_slist_preserves_elements
    (let [result (mutable-list->slist (list->mutable-list (list 10 20 30)))]
      (begin
        (check-equal? 3 (slist/length result))
        (check-equal? 10 (slist/nth result 0))
        (check-equal? 20 (slist/nth result 1))
        (check-equal? 30 (slist/nth result 2)))))

  ;; Conversion: slist->list

  (test-case slist_to_list_empty
    (check-true (list/empty? (slist->list (slist)))))

  (test-case slist_to_list_preserves_elements
    (let [result (slist->list (slist 10 20 30))]
      (begin
        (check-equal? 3 (list/count result))
        (check-equal? 10 (list/nth result 0))
        (check-equal? 20 (list/nth result 1))
        (check-equal? 30 (list/nth result 2)))))

  ;; Conversion: slist->array

  (test-case slist_to_array_empty
    (check-true (array/empty? (slist->array (slist)))))

  (test-case slist_to_array_preserves_elements
    (let [result (slist->array (slist 10 20 30))]
      (begin
        (check-equal? 3 (array/count result))
        (check-equal? 10 (array/nth result 0))
        (check-equal? 20 (array/nth result 1))
        (check-equal? 30 (array/nth result 2)))))

  ;; Conversion: slist->mutable-list

  (test-case slist_to_mutable_list_empty
    (check-true (mutable-list/empty? (slist->mutable-list (slist)))))

  (test-case slist_to_mutable_list_preserves_elements
    (let [result (slist->mutable-list (slist 10 20 30))]
      (begin
        (check-equal? 3 (mutable-list/count result))
        (check-equal? 10 (mutable-list/nth result 0))
        (check-equal? 20 (mutable-list/nth result 1))
        (check-equal? 30 (mutable-list/nth result 2)))))

  ;; Conversion: slist->mutable-array

  (test-case slist_to_mutable_array_empty
    (check-true (mutable-array/empty? (slist->mutable-array (slist)))))

  (test-case slist_to_mutable_array_preserves_elements
    (let [result (slist->mutable-array (slist 10 20 30))]
      (begin
        (check-equal? 3 (mutable-array/count result))
        (check-equal? 10 (mutable-array/nth result 0))
        (check-equal? 20 (mutable-array/nth result 1))
        (check-equal? 30 (mutable-array/nth result 2)))))

  ;; Round-trip tests

  (test-case round_trip_via_list
    (let [original (slist 1 2 3)]
      (let [result (list->slist (slist->list original))]
        (begin
          (check-equal? 3 (slist/length result))
          (check-equal? 1 (slist/nth result 0))
          (check-equal? 2 (slist/nth result 1))
          (check-equal? 3 (slist/nth result 2))))))

  (test-case round_trip_via_array
    (let [original (slist 1 2 3)]
      (let [result (array->slist (slist->array original))]
        (begin
          (check-equal? 3 (slist/length result))
          (check-equal? 1 (slist/nth result 0))
          (check-equal? 2 (slist/nth result 1))
          (check-equal? 3 (slist/nth result 2)))))))
