;; concurrent-queue-tests.zs — Tests for Concurrent-Queue operations
(namespace ZScheme.StdLib.Tests)
(module concurrent-queue-tests)

(import zunit)
(import stdlib/concurrent/queue)

(test-suite ConcurrentQueueTests
  (test-case new_queue_is_empty
    (check-true (empty? (concurrent-queue/new))))

  (test-case new_queue_has_zero_count
    (check-equal? 0 (length (concurrent-queue/new))))

  (test-case enqueue_increases_count
    (let ([q (concurrent-queue/new)])
      (enqueue! q 42)
      (check-equal? 1 (length q))))

  (test-case enqueue_makes_not_empty
    (let ([q (concurrent-queue/new)])
      (enqueue! q 1)
      (check-false (empty? q))))

  (test-case enqueue_multiple_items
    (let ([q (concurrent-queue/new)])
      (enqueue! q 1)
      (enqueue! q 2)
      (enqueue! q 3)
      (check-equal? 3 (length q))))

  (test-case try_dequeue_fifo_order
    (let ([q (concurrent-queue/new)])
      (enqueue! q 10)
      (enqueue! q 20)
      (let ([result (try-dequeue! q)])
        (check-true (value/0 result))
        (check-equal? 10 (value/1 result)))))

  (test-case try_dequeue_from_empty
    (let ([q : (Concurrent-Queue Int) (concurrent-queue/new)])
      (let ([result (try-dequeue! q)])
        (check-false (value/0 result)))))

  (test-case try_peek_returns_front
    (let ([q (concurrent-queue/new)])
      (enqueue! q 42)
      (enqueue! q 99)
      (let ([result (try-peek q)])
        (check-true (value/0 result))
        (check-equal? 42 (value/1 result)))))

  (test-case try_peek_does_not_remove
    (let ([q (concurrent-queue/new)])
      (enqueue! q 42)
      (try-peek q)
      (check-equal? 1 (length q)))))
