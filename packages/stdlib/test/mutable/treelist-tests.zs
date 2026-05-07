;; mutable-treelist-tests.zs — Tests for Mutable-TreeList operations
(namespace ZScheme.StdLib.Tests)
(module mutable-treelist-tests)

(import zunit)
(import stdlib/mutable/treelist)

(test-suite MutableTreeListTests
  (test-case count_returns_length
    (check-equal? 3 (mutable-treelist-length (treelist-copy (treelist 1 2 3)))))

  (test-case nth_returns_element
    (check-equal? 20 (mutable-treelist-ref (treelist-copy (treelist 10 20 30)) 1)))

  (test-case set_replaces_element
    (let [xs (treelist-copy (treelist 1 2 3))]
      (begin
        (mutable-treelist-set! xs 1 99)
        (check-equal? 99 (mutable-treelist-ref xs 1)))))

  (test-case add_appends_to_end
    (let [xs (treelist-copy (treelist 1 2 3))]
      (begin
        (mutable-treelist-add! xs 4)
        (check-equal? 4 (mutable-treelist-length xs))
        (check-equal? 4 (mutable-treelist-ref xs 3)))))

  (test-case insert_at_index
    (let [xs (treelist-copy (treelist 1 3))]
      (begin
        (mutable-treelist-insert! xs 1 2)
        (check-equal? 3 (mutable-treelist-length xs))
        (check-equal? 2 (mutable-treelist-ref xs 1)))))

  (test-case delete_at_index
    (let [xs (treelist-copy (treelist 1 2 3))]
      (begin
        (mutable-treelist-delete! xs 1)
        (check-equal? 2 (mutable-treelist-length xs))
        (check-equal? 3 (mutable-treelist-ref xs 1)))))

  (test-case clear_removes_all
    (let [xs (treelist-copy (treelist 1 2 3))]
      (begin
        (mutable-treelist-clear! xs)
        (check-equal? 0 (mutable-treelist-length xs)))))

  (test-case member_returns_true
    (check-true (mutable-treelist-member? (treelist-copy (treelist 1 2 3)) 2)))

  (test-case member_returns_false
    (check-false (mutable-treelist-member? (treelist-copy (treelist 1 2 3)) 5)))

  (test-case empty_on_empty_treelist
    (check-true (mutable-treelist-empty? (treelist-copy (treelist)))))

  (test-case empty_on_nonempty_treelist
    (check-false (mutable-treelist-empty? (treelist-copy (treelist 1))))))
