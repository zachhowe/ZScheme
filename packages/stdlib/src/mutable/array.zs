;; mutable-array.zs — Mutable-Array operations via T[]
(module mutable-array)

;; CLR bindings (internal)
(import-clr
  System
  [ma-length-raw System.Array.Length
    :instance-property : (Fn [(Mutable-Array ^a)] Int)]
  [ma-item-raw System.Array.Item
    :instance-indexer : (Fn [(Mutable-Array ^a) Int] ^a)]
  [ma-set-item-raw System.Array.Item
    :instance-indexer-set : (Fn [(Mutable-Array ^a) Int ^a] Unit)])

;; Exported functions

(define (mutable-array/count [xs : (Mutable-Array ^a)]) : Int
  (ma-length-raw xs))

(define (mutable-array/nth [xs : (Mutable-Array ^a)] [i : Int]) : ^a
  (ma-item-raw xs i))

(define (mutable-array/set! [xs : (Mutable-Array ^a)] [i : Int] [val : ^a]) : Unit
  (ma-set-item-raw xs i val))

(define (mutable-array/empty? [xs : (Mutable-Array ^a)]) : Bool
  (= (ma-length-raw xs) 0))

(export mutable-array/count mutable-array/nth mutable-array/set! mutable-array/empty?)
