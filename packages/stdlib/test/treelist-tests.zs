;; treelist-tests.zs — Tests for TreeList operations (AVL-backed via ImmutableList<T>)
(namespace ZScheme.StdLib.Tests)
(module treelist-tests)

(import zunit)
(import stdlib/treelist)
(import stdlib/mutable/treelist)
(import stdlib/option)

(test-suite TreeListTests
  (test-case count_returns_length
    (check-equal? 3 (treelist-length (treelist 1 2 3))))

  (test-case nth_returns_element
    (check-equal? 20 (treelist-ref (treelist 10 20 30) 1)))

  (test-case head_returns_first
    (check-equal? 1 (treelist-first (treelist 1 2 3))))

  (test-case last_returns_final_element
    (check-equal? 3 (treelist-last (treelist 1 2 3))))

  (test-case tail_removes_first
    (check-equal? 2 (treelist-length (treelist-rest (treelist 1 2 3)))))

  (test-case cons_prepends
    (check-equal? 0 (treelist-first (treelist-cons 0 (treelist 1 2 3)))))

  (test-case add_appends_to_end
    (check-equal? 4 (treelist-length (treelist-add (treelist 1 2 3) 4))))

  (test-case insert_at_position
    (let [result (treelist-insert (treelist 1 3) 1 2)]
      (begin
        (check-equal? 3 (treelist-length result))
        (check-equal? 2 (treelist-ref result 1)))))

  (test-case delete_at_position
    (let [result (treelist-delete (treelist 1 2 3) 1)]
      (begin
        (check-equal? 2 (treelist-length result))
        (check-equal? 3 (treelist-ref result 1)))))

  (test-case set_replaces_at_position
    (let [result (treelist-set (treelist 1 2 3) 1 99)]
      (check-equal? 99 (treelist-ref result 1))))

  (test-case make_treelist_fills
    (let [result (make-treelist 4 7)]
      (begin
        (check-equal? 4 (treelist-length result))
        (check-equal? 7 (treelist-ref result 0))
        (check-equal? 7 (treelist-ref result 3)))))

  (test-case append_joins_lists
    (check-equal? 5 (treelist-length (treelist-append (treelist 1 2) (treelist 3 4 5)))))

  (test-case append_variadic_three
    (check-equal? 6 (treelist-length
      (treelist-append (treelist 1 2) (treelist 3 4) (treelist 5 6)))))

  (test-case append_variadic_one
    (check-equal? 3 (treelist-length (treelist-append (treelist 1 2 3)))))

  (test-case append_star_concatenates
    (let [result (treelist-append* (treelist (treelist 1 2) (treelist 3) (treelist 4 5)))]
      (begin
        (check-equal? 5 (treelist-length result))
        (check-equal? 1 (treelist-ref result 0))
        (check-equal? 5 (treelist-ref result 4)))))

  (test-case take_first_n
    (let [result (treelist-take (treelist 1 2 3 4 5) 3)]
      (begin
        (check-equal? 3 (treelist-length result))
        (check-equal? 3 (treelist-ref result 2)))))

  (test-case take_zero
    (check-true (treelist-empty? (treelist-take (treelist 1 2 3) 0))))

  (test-case drop_first_n
    (let [result (treelist-drop (treelist 1 2 3 4 5) 2)]
      (begin
        (check-equal? 3 (treelist-length result))
        (check-equal? 3 (treelist-ref result 0)))))

  (test-case take_right_last_n
    (let [result (treelist-take-right (treelist 1 2 3 4 5) 2)]
      (begin
        (check-equal? 2 (treelist-length result))
        (check-equal? 4 (treelist-ref result 0))
        (check-equal? 5 (treelist-ref result 1)))))

  (test-case drop_right_last_n
    (let [result (treelist-drop-right (treelist 1 2 3 4 5) 2)]
      (begin
        (check-equal? 3 (treelist-length result))
        (check-equal? 3 (treelist-ref result 2)))))

  (test-case sublist_range
    (let [result (treelist-sublist (treelist 10 20 30 40 50) 1 4)]
      (begin
        (check-equal? 3 (treelist-length result))
        (check-equal? 20 (treelist-ref result 0))
        (check-equal? 40 (treelist-ref result 2)))))

  (test-case reverse_flips_order
    (let [result (treelist-reverse (treelist 1 2 3))]
      (begin
        (check-equal? 3 (treelist-ref result 0))
        (check-equal? 1 (treelist-ref result 2)))))

  (test-case reverse_empty_stays_empty
    (check-true (treelist-empty? (treelist-reverse (treelist)))))

  (test-case empty_on_empty_list
    (check-true (treelist-empty? (treelist))))

  (test-case empty_on_nonempty_list
    (check-false (treelist-empty? (treelist 1))))

  (test-case member_present
    (check-true (treelist-member? (treelist 1 2 3) 2)))

  (test-case member_absent
    (check-false (treelist-member? (treelist 1 2 3) 99)))

  (test-case index_of_found
    (check-equal? (Some 1) (treelist-index-of (treelist 10 20 30) 20)))

  (test-case index_of_missing
    (check-true (none? (treelist-index-of (treelist 10 20 30) 99))))

  (test-case find_returns_some_on_match
    (check-equal? (Some 4) (treelist-find (treelist 1 2 3 4 5) (lambda (x) (> x 3)))))

  (test-case find_returns_none_on_no_match
    (check-true (none? (treelist-find (treelist 1 2 3) (lambda (x) (> x 99))))))

  (test-case map_transforms_elements
    (let [result (treelist-map (treelist 1 2 3) (lambda (x) (* x 2)))]
      (begin
        (check-equal? 3 (treelist-length result))
        (check-equal? 2 (treelist-ref result 0))
        (check-equal? 4 (treelist-ref result 1))
        (check-equal? 6 (treelist-ref result 2)))))

  (test-case filter_selects_matching
    (let [result (treelist-filter (treelist 1 2 3 4 5) (lambda (x) (> x 3)))]
      (begin
        (check-equal? 2 (treelist-length result))
        (check-equal? 4 (treelist-ref result 0))
        (check-equal? 5 (treelist-ref result 1)))))

  (test-case fold_accumulates
    (check-equal? 15 (treelist-fold (treelist 1 2 3 4 5) 0 (lambda (acc x) (+ acc x)))))

  (test-case for_each_visits_all
    (let [counter (mutable-treelist)]
      (begin
        (treelist-for-each (treelist 10 20 30)
          (lambda (x) (mutable-treelist-add! counter x)))
        (check-equal? 3 (mutable-treelist-length counter))
        (check-equal? 30 (mutable-treelist-ref counter 2)))))

  (test-case sort_orders_ascending
    (let [result (treelist-sort (treelist 3 1 4 1 5 9 2 6) (lambda (a b) (< a b)))]
      (begin
        (check-equal? 8 (treelist-length result))
        (check-equal? 1 (treelist-ref result 0))
        (check-equal? 1 (treelist-ref result 1))
        (check-equal? 2 (treelist-ref result 2))
        (check-equal? 9 (treelist-ref result 7)))))

  (test-case sort_empty_stays_empty
    (check-true (treelist-empty? (treelist-sort (treelist) (lambda (a b) (< a b))))))

  (test-case to_vector_round_trips
    (let [v (treelist->vector (treelist 1 2 3))]
      (check-equal? 3 (treelist-length (vector->treelist v)))))

  (test-case first_returns_head_after_cons
    (check-equal? 1 (treelist-first (treelist-cons 1 (treelist-cons 2 (treelist))))))

  (test-case rest_drops_first_after_cons
    (check-equal? 2 (treelist-first (treelist-rest (treelist-cons 1 (treelist-cons 2 (treelist)))))))

  (test-case cons_builds_treelist
    (check-equal? 3 (treelist-length (treelist-cons 1 (treelist-cons 2 (treelist-cons 3 (treelist))))))))
