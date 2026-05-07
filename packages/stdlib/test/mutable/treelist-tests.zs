;; mutable-treelist-tests.zs — Tests for Mutable-TreeList operations
(namespace ZScheme.StdLib.Tests)
(module mutable-treelist-tests)

(import zunit)
(import stdlib/mutable/treelist)

(test-suite MutableTreeListTests
  (test-case count_returns_length
    (check-equal? 3 (length (treelist->mutable-treelist (treelist 1 2 3)))))

  (test-case nth_returns_element
    (check-equal? 20 (list-ref (treelist->mutable-treelist (treelist 10 20 30)) 1)))

  (test-case set_replaces_element
    (let [xs (treelist->mutable-treelist (treelist 1 2 3))]
      (begin
        (list-set! xs 1 99)
        (check-equal? 99 (list-ref xs 1)))))

  (test-case add_appends_to_end
    (let [xs (treelist->mutable-treelist (treelist 1 2 3))]
      (begin
        (add! xs 4)
        (check-equal? 4 (length xs))
        (check-equal? 4 (list-ref xs 3)))))

  (test-case insert_at_index
    (let [xs (treelist->mutable-treelist (treelist 1 3))]
      (begin
        (insert! xs 1 2)
        (check-equal? 3 (length xs))
        (check-equal? 2 (list-ref xs 1)))))

  (test-case remove_at_index
    (let [xs (treelist->mutable-treelist (treelist 1 2 3))]
      (begin
        (remove-at! xs 1)
        (check-equal? 2 (length xs))
        (check-equal? 3 (list-ref xs 1)))))

  (test-case clear_removes_all
    (let [xs (treelist->mutable-treelist (treelist 1 2 3))]
      (begin
        (clear! xs)
        (check-equal? 0 (length xs)))))

  (test-case contains_returns_true
    (check-true (contains? (treelist->mutable-treelist (treelist 1 2 3)) 2)))

  (test-case contains_returns_false
    (check-false (contains? (treelist->mutable-treelist (treelist 1 2 3)) 5)))

  (test-case empty_on_empty_treelist
    (check-true (empty? (treelist->mutable-treelist (treelist)))))

  (test-case empty_on_nonempty_treelist
    (check-false (empty? (treelist->mutable-treelist (treelist 1))))))
