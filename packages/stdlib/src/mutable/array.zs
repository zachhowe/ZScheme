;; mutable-array.zs — Mutable-Array operations via T[]
(module mutable-array)

;; Map the ZScheme name `Mutable-Array` to a CLR single-dimension array (T[]) at codegen.
(define-type-alias (Mutable-Array ^a) :array)

;; CLR bindings (internal)
(import-clr
  System
  System.Linq
  [ma-length-raw System.Array.Length
    :instance-property : ((Mutable-Array ^a) -> Int)]
  [ma-item-raw System.Array.Item
    :instance-indexer : ((Mutable-Array ^a) Int -> ^a)]
  [ma-set-item-raw System.Array.Item
    :instance-indexer-set : ((Mutable-Array ^a) Int ^a -> Unit)]
  [array-to-mutable-raw System.Linq.Enumerable/ToArray ^a
    : ((Array ^a) -> (Mutable-Array ^a))])

;; Exported functions

(define (array-length [xs : (Mutable-Array ^a)]) : Int
  (ma-length-raw xs))

(define (array-ref [xs : (Mutable-Array ^a)] [i : Int]) : ^a
  (ma-item-raw xs i))

(define (array-set! [xs : (Mutable-Array ^a)] [i : Int] [val : ^a]) : Unit
  (ma-set-item-raw xs i val))

(define (array-empty? [xs : (Mutable-Array ^a)]) : Bool
  (= (ma-length-raw xs) 0))

;; Conversions

;; Array -> Mutable-Array via Enumerable.ToArray<T>.
(define (array->mutable-array [xs : (Array ^a)]) : (Mutable-Array ^a)
  (array-to-mutable-raw xs))

(export array-length array-ref array-set! array-empty? array->mutable-array)
