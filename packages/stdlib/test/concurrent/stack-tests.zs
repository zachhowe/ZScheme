;; concurrent-stack-tests.zs — Tests for Concurrent-Stack operations
(namespace ZScheme.StdLib.Tests)
(module concurrent-stack-tests)

(import zunit)
(import stdlib/concurrent/stack)

(test-suite ConcurrentStackTests
  (test-case new_stack_is_empty
    (check-true (empty? (concurrent-stack/new))))

  (test-case new_stack_has_zero_count
    (check-equal? 0 (length (concurrent-stack/new))))

  (test-case push_increases_count
    (let ([s (concurrent-stack/new)])
      (push! s 42)
      (check-equal? 1 (length s))))

  (test-case push_makes_not_empty
    (let ([s (concurrent-stack/new)])
      (push! s 1)
      (check-false (empty? s))))

  (test-case push_multiple_items
    (let ([s (concurrent-stack/new)])
      (push! s 1)
      (push! s 2)
      (push! s 3)
      (check-equal? 3 (length s))))

  (test-case try_pop_lifo_order
    (let ([s (concurrent-stack/new)])
      (push! s 10)
      (push! s 20)
      (let ([result (try-pop! s)])
        (check-true (value/0 result))
        (check-equal? 20 (value/1 result)))))

  (test-case try_pop_from_empty
    (let ([s : (Concurrent-Stack Int) (concurrent-stack/new)])
      (let ([result (try-pop! s)])
        (check-false (value/0 result)))))

  (test-case try_peek_returns_top
    (let ([s (concurrent-stack/new)])
      (push! s 42)
      (let ([result (try-peek s)])
        (check-true (value/0 result))
        (check-equal? 42 (value/1 result)))))

  (test-case try_peek_does_not_remove
    (let ([s (concurrent-stack/new)])
      (push! s 42)
      (try-peek s)
      (check-equal? 1 (length s))))

  (test-case clear_removes_all
    (let ([s (concurrent-stack/new)])
      (push! s 1)
      (push! s 2)
      (clear! s)
      (check-true (empty? s)))))
