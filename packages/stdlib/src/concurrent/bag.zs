;; concurrent-bag.zs — Concurrent-Bag operations via ConcurrentBag<T>
(module concurrent-bag)

;; Map the ZScheme name `Concurrent-Bag` to System.Collections.Concurrent.ConcurrentBag<T> at codegen.
(define-type-alias (Concurrent-Bag ^a)
  System.Collections.Concurrent.ConcurrentBag :from "System.Collections.Concurrent")

;; CLR bindings (internal)
(import-clr
  System.Collections.Concurrent
  [cb-count-raw System.Collections.Concurrent.ConcurrentBag.Count
    :instance-property : ((Concurrent-Bag ^a) -> Int)]
  [cb-is-empty-raw System.Collections.Concurrent.ConcurrentBag.IsEmpty
    :instance-property : ((Concurrent-Bag ^a) -> Bool)]
  [cb-add-raw System.Collections.Concurrent.ConcurrentBag.Add
    :instance : ((Concurrent-Bag ^a) ^a -> Unit)]
  [cb-try-take-raw System.Collections.Concurrent.ConcurrentBag.TryTake
    :instance : ((Concurrent-Bag ^a) -> (ValueTuple Bool ^a))]
  [cb-try-peek-raw System.Collections.Concurrent.ConcurrentBag.TryPeek
    :instance : ((Concurrent-Bag ^a) -> (ValueTuple Bool ^a))])

;; Exported functions

;; Create an empty concurrent bag
(define (concurrent-bag/new) : (Concurrent-Bag ^a)
  (new (System.Collections.Concurrent.ConcurrentBag ^a)))

(define (length [bag : (Concurrent-Bag ^a)]) : Int
  (cb-count-raw bag))

(define (empty? [bag : (Concurrent-Bag ^a)]) : Bool
  (cb-is-empty-raw bag))

(define (add! [bag : (Concurrent-Bag ^a)] [val : ^a]) : Unit
  (cb-add-raw bag val))

;; Try to take an item from the bag. Returns (Bool, ^a) tuple.
(define (try-take! [bag : (Concurrent-Bag ^a)]) : (ValueTuple Bool ^a)
  (cb-try-take-raw bag))

;; Try to peek at an item in the bag. Returns (Bool, ^a) tuple.
(define (try-peek [bag : (Concurrent-Bag ^a)]) : (ValueTuple Bool ^a)
  (cb-try-peek-raw bag))

(export concurrent-bag/new length empty? add! try-take! try-peek)
