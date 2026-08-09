;; concurrent-queue.zs — Concurrent-Queue operations via ConcurrentQueue<T>
(module concurrent-queue)

;; Map the ZScheme name `Concurrent-Queue` to System.Collections.Concurrent.ConcurrentQueue<T> at codegen.
(define-type-alias (Concurrent-Queue ^a)
  System.Collections.Concurrent.ConcurrentQueue :from "System.Collections.Concurrent")

;; CLR bindings (internal)
(import-clr
  System.Collections.Concurrent
  [cq-count-raw ConcurrentQueue.Count
    :instance-property : ((Concurrent-Queue ^a) -> Int)]
  [cq-is-empty-raw ConcurrentQueue.IsEmpty
    :instance-property : ((Concurrent-Queue ^a) -> Bool)]
  [cq-enqueue-raw ConcurrentQueue.Enqueue
    :instance : ((Concurrent-Queue ^a) ^a -> Unit)]
  [cq-try-dequeue-raw ConcurrentQueue.TryDequeue
    :instance : ((Concurrent-Queue ^a) -> (ValueTuple Bool ^a))]
  [cq-try-peek-raw ConcurrentQueue.TryPeek
    :instance : ((Concurrent-Queue ^a) -> (ValueTuple Bool ^a))])

;; Exported functions

;; Create an empty concurrent queue
(define (concurrent-queue/new) : (Concurrent-Queue ^a)
  (new (ConcurrentQueue ^a)))

(define (length [q : (Concurrent-Queue ^a)]) : Int
  (cq-count-raw q))

(define (empty? [q : (Concurrent-Queue ^a)]) : Bool
  (cq-is-empty-raw q))

(define (enqueue! [q : (Concurrent-Queue ^a)] [val : ^a]) : Unit
  (cq-enqueue-raw q val))

;; Try to dequeue an item. Returns (Bool, ^a) tuple.
(define (try-dequeue! [q : (Concurrent-Queue ^a)]) : (ValueTuple Bool ^a)
  (cq-try-dequeue-raw q))

;; Try to peek at the front item. Returns (Bool, ^a) tuple.
(define (try-peek [q : (Concurrent-Queue ^a)]) : (ValueTuple Bool ^a)
  (cq-try-peek-raw q))

(export concurrent-queue/new length empty? enqueue! try-dequeue! try-peek)
