;; concurrent-stack-tests.zs — Tests for Concurrent-Stack operations
(namespace ZScheme.StdLib.Tests)
(module concurrent-stack-tests)

(import zunit)
(import stdlib/concurrent-stack)

(test-suite ConcurrentStackTests
  (test-case new_stack_is_empty
    (check-true (concurrent-stack/empty? (concurrent-stack/new))))

  (test-case new_stack_has_zero_count
    (check-equal? 0 (concurrent-stack/count (concurrent-stack/new))))

  (test-case push_increases_count
    (let [s (concurrent-stack/new)]
      (begin
        (concurrent-stack/push! s 42)
        (check-equal? 1 (concurrent-stack/count s)))))

  (test-case push_makes_not_empty
    (let [s (concurrent-stack/new)]
      (begin
        (concurrent-stack/push! s 1)
        (check-false (concurrent-stack/empty? s)))))

  (test-case push_multiple_items
    (let [s (concurrent-stack/new)]
      (begin
        (concurrent-stack/push! s 1)
        (concurrent-stack/push! s 2)
        (concurrent-stack/push! s 3)
        (check-equal? 3 (concurrent-stack/count s)))))

  (test-case try_pop_lifo_order
    (let [s (concurrent-stack/new)]
      (begin
        (concurrent-stack/push! s 10)
        (concurrent-stack/push! s 20)
        (let [result (concurrent-stack/try-pop! s)]
          (begin
            (check-true (tuple/first result))
            (check-equal? 20 (tuple/second result)))))))

  (test-case try_pop_from_empty
    (let [s : (Concurrent-Stack Int) (concurrent-stack/new)]
      (let [result (concurrent-stack/try-pop! s)]
        (check-false (tuple/first result)))))

  (test-case try_peek_returns_top
    (let [s (concurrent-stack/new)]
      (begin
        (concurrent-stack/push! s 42)
        (let [result (concurrent-stack/try-peek s)]
          (begin
            (check-true (tuple/first result))
            (check-equal? 42 (tuple/second result)))))))

  (test-case try_peek_does_not_remove
    (let [s (concurrent-stack/new)]
      (begin
        (concurrent-stack/push! s 42)
        (concurrent-stack/try-peek s)
        (check-equal? 1 (concurrent-stack/count s)))))

  (test-case clear_removes_all
    (let [s (concurrent-stack/new)]
      (begin
        (concurrent-stack/push! s 1)
        (concurrent-stack/push! s 2)
        (concurrent-stack/clear! s)
        (check-true (concurrent-stack/empty? s))))))
