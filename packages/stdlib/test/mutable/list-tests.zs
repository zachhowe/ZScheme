;; mutable-list-tests.zs — Tests for Mutable-List operations
(namespace ZScheme.StdLib.Tests)
(module mutable-list-tests)

(import zunit)
(import stdlib/mutable/list)

(test-suite MutableListTests
  (test-case count_returns_length
    (check-equal? 3 (mutable-list/count (list->mutable-list (list 1 2 3)))))

  (test-case nth_returns_element
    (check-equal? 20 (mutable-list/nth (list->mutable-list (list 10 20 30)) 1)))

  (test-case set_replaces_element
    (let [xs (list->mutable-list (list 1 2 3))]
      (begin
        (mutable-list/set! xs 1 99)
        (check-equal? 99 (mutable-list/nth xs 1)))))

  (test-case add_appends_to_end
    (let [xs (list->mutable-list (list 1 2 3))]
      (begin
        (mutable-list/add! xs 4)
        (check-equal? 4 (mutable-list/count xs))
        (check-equal? 4 (mutable-list/nth xs 3)))))

  (test-case insert_at_index
    (let [xs (list->mutable-list (list 1 3))]
      (begin
        (mutable-list/insert! xs 1 2)
        (check-equal? 3 (mutable-list/count xs))
        (check-equal? 2 (mutable-list/nth xs 1)))))

  (test-case remove_at_index
    (let [xs (list->mutable-list (list 1 2 3))]
      (begin
        (mutable-list/remove-at! xs 1)
        (check-equal? 2 (mutable-list/count xs))
        (check-equal? 3 (mutable-list/nth xs 1)))))

  (test-case clear_removes_all
    (let [xs (list->mutable-list (list 1 2 3))]
      (begin
        (mutable-list/clear! xs)
        (check-equal? 0 (mutable-list/count xs)))))

  (test-case contains_returns_true
    (check-true (mutable-list/contains? (list->mutable-list (list 1 2 3)) 2)))

  (test-case contains_returns_false
    (check-false (mutable-list/contains? (list->mutable-list (list 1 2 3)) 5)))

  (test-case empty_on_empty_list
    (check-true (mutable-list/empty? (list->mutable-list (list)))))

  (test-case empty_on_nonempty_list
    (check-false (mutable-list/empty? (list->mutable-list (list 1))))))
