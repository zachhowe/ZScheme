;; concurrent-queue.zs — Concurrent-Queue operations via ConcurrentQueue<T>
(module concurrent-queue)

;; CLR bindings (internal)
(import-clr
  System.Collections.Concurrent
  [cq-count-raw System.Collections.Concurrent.ConcurrentQueue.Count
    :instance-property : (Fn [(Concurrent-Queue ^a)] Int)]
  [cq-is-empty-raw System.Collections.Concurrent.ConcurrentQueue.IsEmpty
    :instance-property : (Fn [(Concurrent-Queue ^a)] Bool)]
  [cq-enqueue-raw System.Collections.Concurrent.ConcurrentQueue.Enqueue
    :instance : (Fn [(Concurrent-Queue ^a) ^a] Unit)]
  [cq-try-dequeue-raw System.Collections.Concurrent.ConcurrentQueue.TryDequeue
    :instance : (Fn [(Concurrent-Queue ^a)] (ValueTuple Bool ^a))]
  [cq-try-peek-raw System.Collections.Concurrent.ConcurrentQueue.TryPeek
    :instance : (Fn [(Concurrent-Queue ^a)] (ValueTuple Bool ^a))])

;; Exported functions

;; Create an empty concurrent queue
(define (concurrent-queue/new) : (Concurrent-Queue ^a)
  (new (System.Collections.Concurrent.ConcurrentQueue ^a)))

(define (concurrent-queue/count [q : (Concurrent-Queue ^a)]) : Int
  (cq-count-raw q))

(define (concurrent-queue/empty? [q : (Concurrent-Queue ^a)]) : Bool
  (cq-is-empty-raw q))

(define (concurrent-queue/enqueue! [q : (Concurrent-Queue ^a)] [val : ^a]) : Unit
  (cq-enqueue-raw q val))

;; Try to dequeue an item. Returns (Bool, ^a) tuple.
(define (concurrent-queue/try-dequeue! [q : (Concurrent-Queue ^a)]) : (ValueTuple Bool ^a)
  (cq-try-dequeue-raw q))

;; Try to peek at the front item. Returns (Bool, ^a) tuple.
(define (concurrent-queue/try-peek [q : (Concurrent-Queue ^a)]) : (ValueTuple Bool ^a)
  (cq-try-peek-raw q))

(export concurrent-queue/new concurrent-queue/count concurrent-queue/empty?
        concurrent-queue/enqueue! concurrent-queue/try-dequeue! concurrent-queue/try-peek)
