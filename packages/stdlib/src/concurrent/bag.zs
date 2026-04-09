;; concurrent-bag.zs — Concurrent-Bag operations via ConcurrentBag<T>
(module concurrent-bag)

;; CLR bindings (internal)
(import-clr
  System.Collections.Concurrent
  [cb-count-raw System.Collections.Concurrent.ConcurrentBag.Count
    :instance-property : (Fn [(Concurrent-Bag ^a)] Int)]
  [cb-is-empty-raw System.Collections.Concurrent.ConcurrentBag.IsEmpty
    :instance-property : (Fn [(Concurrent-Bag ^a)] Bool)]
  [cb-add-raw System.Collections.Concurrent.ConcurrentBag.Add
    :instance : (Fn [(Concurrent-Bag ^a) ^a] Unit)]
  [cb-try-take-raw System.Collections.Concurrent.ConcurrentBag.TryTake
    :instance : (Fn [(Concurrent-Bag ^a)] (ValueTuple Bool ^a))]
  [cb-try-peek-raw System.Collections.Concurrent.ConcurrentBag.TryPeek
    :instance : (Fn [(Concurrent-Bag ^a)] (ValueTuple Bool ^a))])

;; Exported functions

;; Create an empty concurrent bag
(define (concurrent-bag/new) : (Concurrent-Bag ^a)
  (new (System.Collections.Concurrent.ConcurrentBag ^a)))

(define (concurrent-bag/count [bag : (Concurrent-Bag ^a)]) : Int
  (cb-count-raw bag))

(define (concurrent-bag/empty? [bag : (Concurrent-Bag ^a)]) : Bool
  (cb-is-empty-raw bag))

(define (concurrent-bag/add! [bag : (Concurrent-Bag ^a)] [val : ^a]) : Unit
  (cb-add-raw bag val))

;; Try to take an item from the bag. Returns (Bool, ^a) tuple.
(define (concurrent-bag/try-take! [bag : (Concurrent-Bag ^a)]) : (ValueTuple Bool ^a)
  (cb-try-take-raw bag))

;; Try to peek at an item in the bag. Returns (Bool, ^a) tuple.
(define (concurrent-bag/try-peek [bag : (Concurrent-Bag ^a)]) : (ValueTuple Bool ^a)
  (cb-try-peek-raw bag))

(export concurrent-bag/new concurrent-bag/count concurrent-bag/empty?
        concurrent-bag/add! concurrent-bag/try-take! concurrent-bag/try-peek)
