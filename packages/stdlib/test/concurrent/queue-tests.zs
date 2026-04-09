;; concurrent-queue-tests.zs — Tests for Concurrent-Queue operations
(namespace ZScheme.StdLib.Tests)
(module concurrent-queue-tests)

(import zunit)
(import stdlib/concurrent/queue)

(test-suite ConcurrentQueueTests
  (test-case new_queue_is_empty
    (check-true (concurrent-queue/empty? (concurrent-queue/new))))

  (test-case new_queue_has_zero_count
    (check-equal? 0 (concurrent-queue/count (concurrent-queue/new))))

  (test-case enqueue_increases_count
    (let [q (concurrent-queue/new)]
      (begin
        (concurrent-queue/enqueue! q 42)
        (check-equal? 1 (concurrent-queue/count q)))))

  (test-case enqueue_makes_not_empty
    (let [q (concurrent-queue/new)]
      (begin
        (concurrent-queue/enqueue! q 1)
        (check-false (concurrent-queue/empty? q)))))

  (test-case enqueue_multiple_items
    (let [q (concurrent-queue/new)]
      (begin
        (concurrent-queue/enqueue! q 1)
        (concurrent-queue/enqueue! q 2)
        (concurrent-queue/enqueue! q 3)
        (check-equal? 3 (concurrent-queue/count q)))))

  (test-case try_dequeue_fifo_order
    (let [q (concurrent-queue/new)]
      (begin
        (concurrent-queue/enqueue! q 10)
        (concurrent-queue/enqueue! q 20)
        (let [result (concurrent-queue/try-dequeue! q)]
          (begin
            (check-true (value/0 result))
            (check-equal? 10 (value/1 result)))))))

  (test-case try_dequeue_from_empty
    (let [q : (Concurrent-Queue Int) (concurrent-queue/new)]
      (let [result (concurrent-queue/try-dequeue! q)]
        (check-false (value/0 result)))))

  (test-case try_peek_returns_front
    (let [q (concurrent-queue/new)]
      (begin
        (concurrent-queue/enqueue! q 42)
        (concurrent-queue/enqueue! q 99)
        (let [result (concurrent-queue/try-peek q)]
          (begin
            (check-true (value/0 result))
            (check-equal? 42 (value/1 result)))))))

  (test-case try_peek_does_not_remove
    (let [q (concurrent-queue/new)]
      (begin
        (concurrent-queue/enqueue! q 42)
        (concurrent-queue/try-peek q)
        (check-equal? 1 (concurrent-queue/count q))))))
