;; concurrent-stack.zs — Concurrent-Stack operations via ConcurrentStack<T>
(module concurrent-stack)

;; Map the ZScheme name `Concurrent-Stack` to System.Collections.Concurrent.ConcurrentStack<T> at codegen.
(define-type-alias (Concurrent-Stack ^a)
  System.Collections.Concurrent.ConcurrentStack :from "System.Collections.Concurrent")

;; CLR bindings (internal)
(import-clr
  System.Collections.Concurrent
  [cs-count-raw System.Collections.Concurrent.ConcurrentStack.Count
    :instance-property : ((Concurrent-Stack ^a) -> Int)]
  [cs-is-empty-raw System.Collections.Concurrent.ConcurrentStack.IsEmpty
    :instance-property : ((Concurrent-Stack ^a) -> Bool)]
  [cs-push-raw System.Collections.Concurrent.ConcurrentStack.Push
    :instance : ((Concurrent-Stack ^a) ^a -> Unit)]
  [cs-clear-raw System.Collections.Concurrent.ConcurrentStack.Clear
    :instance : ((Concurrent-Stack ^a) -> Unit)]
  [cs-try-pop-raw System.Collections.Concurrent.ConcurrentStack.TryPop
    :instance : ((Concurrent-Stack ^a) -> (ValueTuple Bool ^a))]
  [cs-try-peek-raw System.Collections.Concurrent.ConcurrentStack.TryPeek
    :instance : ((Concurrent-Stack ^a) -> (ValueTuple Bool ^a))])

;; Exported functions

;; Create an empty concurrent stack
(define (concurrent-stack/new) : (Concurrent-Stack ^a)
  (new (System.Collections.Concurrent.ConcurrentStack ^a)))

(define (length [s : (Concurrent-Stack ^a)]) : Int
  (cs-count-raw s))

(define (empty? [s : (Concurrent-Stack ^a)]) : Bool
  (cs-is-empty-raw s))

(define (push! [s : (Concurrent-Stack ^a)] [val : ^a]) : Unit
  (cs-push-raw s val))

(define (clear! [s : (Concurrent-Stack ^a)]) : Unit
  (cs-clear-raw s))

;; Try to pop an item. Returns (Bool, ^a) tuple.
(define (try-pop! [s : (Concurrent-Stack ^a)]) : (ValueTuple Bool ^a)
  (cs-try-pop-raw s))

;; Try to peek at the top item. Returns (Bool, ^a) tuple.
(define (try-peek [s : (Concurrent-Stack ^a)]) : (ValueTuple Bool ^a)
  (cs-try-peek-raw s))

(export concurrent-stack/new length empty? push! clear! try-pop! try-peek)
