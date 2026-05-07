;; treelist-tests.zs — Tests for TreeList operations (AVL-backed via ImmutableList<T>)
(namespace ZScheme.StdLib.Tests)
(module treelist-tests)

(import zunit)
(import stdlib/treelist)

(test-suite TreeListTests
  (test-case count_returns_length
    (check-equal? 3 (treelist-length (treelist 1 2 3))))

  (test-case nth_returns_element
    (check-equal? 20 (treelist-ref (treelist 10 20 30) 1)))

  (test-case head_returns_first
    (check-equal? 1 (treelist-first (treelist 1 2 3))))

  (test-case tail_removes_first
    (check-equal? 2 (treelist-length (treelist-rest (treelist 1 2 3)))))

  (test-case cons_prepends
    (check-equal? 0 (treelist-first (treelist-cons 0 (treelist 1 2 3)))))

  (test-case add_appends_to_end
    (check-equal? 4 (treelist-length (treelist-add (treelist 1 2 3) 4))))

  (test-case append_joins_lists
    (check-equal? 5 (treelist-length (treelist-append (treelist 1 2) (treelist 3 4 5)))))

  (test-case empty_on_empty_list
    (check-true (treelist-empty? (treelist))))

  (test-case empty_on_nonempty_list
    (check-false (treelist-empty? (treelist 1))))

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

  (test-case first_returns_head_after_cons
    (check-equal? 1 (treelist-first (treelist-cons 1 (treelist-cons 2 (treelist))))))

  (test-case rest_drops_first_after_cons
    (check-equal? 2 (treelist-first (treelist-rest (treelist-cons 1 (treelist-cons 2 (treelist)))))))

  (test-case cons_builds_treelist
    (check-equal? 3 (treelist-length (treelist-cons 1 (treelist-cons 2 (treelist-cons 3 (treelist))))))))
