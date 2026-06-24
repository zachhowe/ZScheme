;; mutable-treelist-tests.zs — Tests for Mutable-TreeList operations
(namespace ZScheme.StdLib.Tests)
(module mutable-treelist-tests)

(import zunit)
(import stdlib/treelist)
(import stdlib/mutable/treelist)
(import stdlib/option)

(test-suite MutableTreeListTests
  (test-case count_returns_length
    (check-equal? 3 (mutable-treelist-length (treelist-copy (treelist 1 2 3)))))

  (test-case nth_returns_element
    (check-equal? 20 (mutable-treelist-ref (treelist-copy (treelist 10 20 30)) 1)))

  (test-case variadic_constructor
    (let ([xs (mutable-treelist 10 20 30)])
      (check-equal? 3 (mutable-treelist-length xs))
      (check-equal? 20 (mutable-treelist-ref xs 1))))

  (test-case variadic_constructor_zero_args
    (check-true (mutable-treelist-empty? (mutable-treelist))))

  (test-case make_mutable_treelist_fills
    (let ([xs (make-mutable-treelist 4 7)])
      (check-equal? 4 (mutable-treelist-length xs))
      (check-equal? 7 (mutable-treelist-ref xs 0))
      (check-equal? 7 (mutable-treelist-ref xs 3))))

  (test-case first_returns_head
    (check-equal? 1 (mutable-treelist-first (mutable-treelist 1 2 3))))

  (test-case last_returns_final
    (check-equal? 3 (mutable-treelist-last (mutable-treelist 1 2 3))))

  (test-case set_replaces_element
    (let ([xs (treelist-copy (treelist 1 2 3))])
      (mutable-treelist-set! xs 1 99)
      (check-equal? 99 (mutable-treelist-ref xs 1))))

  (test-case add_appends_to_end
    (let ([xs (treelist-copy (treelist 1 2 3))])
      (mutable-treelist-add! xs 4)
      (check-equal? 4 (mutable-treelist-length xs))
      (check-equal? 4 (mutable-treelist-ref xs 3))))

  (test-case cons_prepends
    (let ([xs (mutable-treelist 1 2 3)])
      (mutable-treelist-cons! 0 xs)
      (check-equal? 4 (mutable-treelist-length xs))
      (check-equal? 0 (mutable-treelist-ref xs 0))))

  (test-case insert_at_index
    (let ([xs (treelist-copy (treelist 1 3))])
      (mutable-treelist-insert! xs 1 2)
      (check-equal? 3 (mutable-treelist-length xs))
      (check-equal? 2 (mutable-treelist-ref xs 1))))

  (test-case delete_at_index
    (let ([xs (treelist-copy (treelist 1 2 3))])
      (mutable-treelist-delete! xs 1)
      (check-equal? 2 (mutable-treelist-length xs))
      (check-equal? 3 (mutable-treelist-ref xs 1))))

  (test-case clear_removes_all
    (let ([xs (treelist-copy (treelist 1 2 3))])
      (mutable-treelist-clear! xs)
      (check-equal? 0 (mutable-treelist-length xs))))

  (test-case append_bang_concatenates
    (let ([xs (mutable-treelist 1 2)])
      (mutable-treelist-append! xs (mutable-treelist 3 4 5))
      (check-equal? 5 (mutable-treelist-length xs))
      (check-equal? 5 (mutable-treelist-ref xs 4))))

  (test-case prepend_bang_concatenates_front
    (let ([xs (mutable-treelist 3 4 5)])
      (mutable-treelist-prepend! xs (mutable-treelist 1 2))
      (check-equal? 5 (mutable-treelist-length xs))
      (check-equal? 1 (mutable-treelist-ref xs 0))
      (check-equal? 5 (mutable-treelist-ref xs 4))))

  (test-case take_bang_keeps_first_n
    (let ([xs (mutable-treelist 1 2 3 4 5)])
      (mutable-treelist-take! xs 3)
      (check-equal? 3 (mutable-treelist-length xs))
      (check-equal? 3 (mutable-treelist-ref xs 2))))

  (test-case drop_bang_removes_first_n
    (let ([xs (mutable-treelist 1 2 3 4 5)])
      (mutable-treelist-drop! xs 2)
      (check-equal? 3 (mutable-treelist-length xs))
      (check-equal? 3 (mutable-treelist-ref xs 0))))

  (test-case take_right_bang_keeps_last_n
    (let ([xs (mutable-treelist 1 2 3 4 5)])
      (mutable-treelist-take-right! xs 2)
      (check-equal? 2 (mutable-treelist-length xs))
      (check-equal? 4 (mutable-treelist-ref xs 0))
      (check-equal? 5 (mutable-treelist-ref xs 1))))

  (test-case drop_right_bang_removes_last_n
    (let ([xs (mutable-treelist 1 2 3 4 5)])
      (mutable-treelist-drop-right! xs 2)
      (check-equal? 3 (mutable-treelist-length xs))
      (check-equal? 3 (mutable-treelist-ref xs 2))))

  (test-case sublist_bang_keeps_range
    (let ([xs (mutable-treelist 10 20 30 40 50)])
      (mutable-treelist-sublist! xs 1 4)
      (check-equal? 3 (mutable-treelist-length xs))
      (check-equal? 20 (mutable-treelist-ref xs 0))
      (check-equal? 40 (mutable-treelist-ref xs 2))))

  (test-case reverse_bang_flips_order
    (let ([xs (mutable-treelist 1 2 3)])
      (mutable-treelist-reverse! xs)
      (check-equal? 3 (mutable-treelist-ref xs 0))
      (check-equal? 1 (mutable-treelist-ref xs 2))))

  (test-case member_returns_true
    (check-true (mutable-treelist-member? (treelist-copy (treelist 1 2 3)) 2)))

  (test-case member_returns_false
    (check-false (mutable-treelist-member? (treelist-copy (treelist 1 2 3)) 5)))

  (test-case index_of_found
    (check-equal? (Some 1) (mutable-treelist-index-of (mutable-treelist 10 20 30) 20)))

  (test-case index_of_missing
    (check-true (none? (mutable-treelist-index-of (mutable-treelist 10 20 30) 99))))

  (test-case find_returns_some
    (check-equal? (Some 4) (mutable-treelist-find (mutable-treelist 1 2 3 4 5) (lambda (x) (> x 3)))))

  (test-case find_returns_none
    (check-true (none? (mutable-treelist-find (mutable-treelist 1 2 3) (lambda (x) (> x 99))))))

  (test-case empty_on_empty_treelist
    (check-true (mutable-treelist-empty? (treelist-copy (treelist)))))

  (test-case empty_on_nonempty_treelist
    (check-false (mutable-treelist-empty? (treelist-copy (treelist 1)))))

  (test-case map_bang_transforms_in_place
    (let ([xs (mutable-treelist 1 2 3)])
      (mutable-treelist-map! xs (lambda (x) (* x 10)))
      (check-equal? 10 (mutable-treelist-ref xs 0))
      (check-equal? 20 (mutable-treelist-ref xs 1))
      (check-equal? 30 (mutable-treelist-ref xs 2))))

  (test-case for_each_visits_all
    (let ([sink (mutable-treelist)])
      (mutable-treelist-for-each (mutable-treelist 10 20 30)
        (lambda (x) (mutable-treelist-add! sink x)))
      (check-equal? 3 (mutable-treelist-length sink))
      (check-equal? 30 (mutable-treelist-ref sink 2))))

  (test-case sort_bang_orders_ascending
    (let ([xs (mutable-treelist 3 1 4 1 5 9 2 6)])
      (mutable-treelist-sort! xs (lambda (a b) (< a b)))
      (check-equal? 1 (mutable-treelist-ref xs 0))
      (check-equal? 1 (mutable-treelist-ref xs 1))
      (check-equal? 2 (mutable-treelist-ref xs 2))
      (check-equal? 9 (mutable-treelist-ref xs 7))))

  (test-case mutable_copy_independent
    (let ([orig (mutable-treelist 1 2 3)])
      (let ([dup (mutable-treelist-copy orig)])
        (mutable-treelist-add! dup 4)
        (check-equal? 3 (mutable-treelist-length orig))
        (check-equal? 4 (mutable-treelist-length dup)))))

  (test-case snapshot_range_extracts_slice
    (let ([xs (mutable-treelist 10 20 30 40 50)])
      (let ([snap (mutable-treelist-snapshot/range xs 1 4)])
        (check-equal? 3 (treelist-length snap))
        (check-equal? 20 (treelist-ref snap 0))
        (check-equal? 40 (treelist-ref snap 2)))))

  (test-case to_vector_round_trips
    (let ([v (mutable-treelist->vector (mutable-treelist 1 2 3))])
      (let ([back (vector->mutable-treelist v)])
        (check-equal? 3 (mutable-treelist-length back))
        (check-equal? 2 (mutable-treelist-ref back 1))))))
