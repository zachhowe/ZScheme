;; concurrent-bag-tests.zs — Tests for Concurrent-Bag operations
(namespace ZScheme.StdLib.Tests)
(module concurrent-bag-tests)

(import zunit)
(import stdlib/concurrent/bag)

(test-suite ConcurrentBagTests
  (test-case new_bag_is_empty
    (check-true (empty? (concurrent-bag/new))))

  (test-case new_bag_has_zero_count
    (check-equal? 0 (length (concurrent-bag/new))))

  (test-case add_increases_count
    (let ([bag (concurrent-bag/new)])
      (begin
        (add! bag 42)
        (check-equal? 1 (length bag)))))

  (test-case add_makes_not_empty
    (let ([bag (concurrent-bag/new)])
      (begin
        (add! bag 1)
        (check-false (empty? bag)))))

  (test-case add_multiple_items
    (let ([bag (concurrent-bag/new)])
      (begin
        (add! bag 1)
        (add! bag 2)
        (add! bag 3)
        (check-equal? 3 (length bag)))))

  (test-case try_take_from_nonempty
    (let ([bag (concurrent-bag/new)])
      (begin
        (add! bag 99)
        (let ([result (try-take! bag)])
          (check-true (value/0 result))))))

  (test-case try_take_from_empty
    (let ([bag : (Concurrent-Bag Int) (concurrent-bag/new)])
      (let ([result (try-take! bag)])
        (check-false (value/0 result)))))

  (test-case try_peek_from_nonempty
    (let ([bag (concurrent-bag/new)])
      (begin
        (add! bag 42)
        (let ([result (try-peek bag)])
          (begin
            (check-true (value/0 result))
            (check-equal? 42 (value/1 result))))))))
