;; concurrent-bag-tests.zs — Tests for Concurrent-Bag operations
(namespace ZScheme.StdLib.Tests)
(module concurrent-bag-tests)

(import zunit)
(import stdlib/concurrent-bag)

(test-suite ConcurrentBagTests
  (test-case new_bag_is_empty
    (check-true (concurrent-bag/empty? (concurrent-bag/new))))

  (test-case new_bag_has_zero_count
    (check-equal? 0 (concurrent-bag/count (concurrent-bag/new))))

  (test-case add_increases_count
    (let [bag (concurrent-bag/new)]
      (begin
        (concurrent-bag/add! bag 42)
        (check-equal? 1 (concurrent-bag/count bag)))))

  (test-case add_makes_not_empty
    (let [bag (concurrent-bag/new)]
      (begin
        (concurrent-bag/add! bag 1)
        (check-false (concurrent-bag/empty? bag)))))

  (test-case add_multiple_items
    (let [bag (concurrent-bag/new)]
      (begin
        (concurrent-bag/add! bag 1)
        (concurrent-bag/add! bag 2)
        (concurrent-bag/add! bag 3)
        (check-equal? 3 (concurrent-bag/count bag)))))

  (test-case try_take_from_nonempty
    (let [bag (concurrent-bag/new)]
      (begin
        (concurrent-bag/add! bag 99)
        (let [result (concurrent-bag/try-take! bag)]
          (check-true (tuple/first result))))))

  (test-case try_take_from_empty
    (let [bag : (Concurrent-Bag Int) (concurrent-bag/new)]
      (let [result (concurrent-bag/try-take! bag)]
        (check-false (tuple/first result)))))

  (test-case try_peek_from_nonempty
    (let [bag (concurrent-bag/new)]
      (begin
        (concurrent-bag/add! bag 42)
        (let [result (concurrent-bag/try-peek bag)]
          (begin
            (check-true (tuple/first result))
            (check-equal? 42 (tuple/second result))))))))
