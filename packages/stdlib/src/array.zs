;; array.zs — Array operations via ImmutableArray<T>
(module array)

;; Pull in the Mutable-Array alias so variadic functions in this module can
;; resolve their synthesized rest-parameter type (Mutable-Array ^a).
(import stdlib/mutable/array)

;; Map the ZScheme name `Array` to System.Collections.Immutable.ImmutableArray<T> at codegen.
(define-type-alias (Array ^a)
  System.Collections.Immutable.ImmutableArray :from "System.Collections.Immutable")

;; CLR bindings (internal)
(import-clr
  System.Collections.Immutable
  [array-length-raw System.Collections.Immutable.ImmutableArray.Length
    :instance-property : ((Array ^a) -> Int)]
  [array-item-raw System.Collections.Immutable.ImmutableArray.Item
    :instance-indexer : ((Array ^a) Int -> ^a)]
  [array-add-raw System.Collections.Immutable.ImmutableArray.Add
    :instance : ((Array ^a) ^a -> (Array ^a))]
  [array-set-raw System.Collections.Immutable.ImmutableArray.SetItem
    :instance : ((Array ^a) Int ^a -> (Array ^a))]
  [array-create System.Collections.Immutable.ImmutableArray/Create ^a
    : ((Mutable-Array ^a) -> (Array ^a))])

;; Constructor
(define (array [elements : ^a ...]) : (Array ^a)
  (array-create elements))

;; Internal loop helpers (defined before the public functions that call them)

(define (array/map-loop [xs : (Array ^a)] [f : (^a -> ^b)] [len : Int] [i : Int] [acc : (Array ^b)]) : (Array ^b)
  (if (= i len)
    acc
    (array/map-loop xs f len (+ i 1) (array-add-raw acc (f (array-item-raw xs i))))))

(define (array/filter-loop [xs : (Array ^a)] [pred : (^a -> Bool)] [len : Int] [i : Int] [acc : (Array ^a)]) : (Array ^a)
  (if (= i len)
    acc
    (let [item (array-item-raw xs i)]
      (if (pred item)
        (array/filter-loop xs pred len (+ i 1) (array-add-raw acc item))
        (array/filter-loop xs pred len (+ i 1) acc)))))

(define (array/fold-loop [xs : (Array ^a)] [f : (^b ^a -> ^b)] [len : Int] [i : Int] [acc : ^b]) : ^b
  (if (= i len)
    acc
    (array/fold-loop xs f len (+ i 1) (f acc (array-item-raw xs i)))))

;; Exported functions

(define (array-length [xs : (Array ^a)]) : Int
  (array-length-raw xs))

(define (array-ref [xs : (Array ^a)] [i : Int]) : ^a
  (array-item-raw xs i))

(define (append [xs : (Array ^a)] [x : ^a]) : (Array ^a)
  (array-add-raw xs x))

(define (set [xs : (Array ^a)] [i : Int] [x : ^a]) : (Array ^a)
  (array-set-raw xs i x))

(define (array-empty? [xs : (Array ^a)]) : Bool
  (= (array-length-raw xs) 0))

(define (map [xs : (Array ^a)] [f : (^a -> ^b)]) : (Array ^b)
  (let [len (array-length-raw xs)]
    (array/map-loop xs f len 0 (array))))

(define (filter [xs : (Array ^a)] [pred : (^a -> Bool)]) : (Array ^a)
  (let [len (array-length-raw xs)]
    (array/filter-loop xs pred len 0 (array))))

(define (fold [xs : (Array ^a)] [init : ^b] [f : (^b ^a -> ^b)]) : ^b
  (let [len (array-length-raw xs)]
    (array/fold-loop xs f len 0 init)))

;; Conversions

;; Mutable-Array -> Array via ImmutableArray.Create<T>(T[]).
(define (mutable-array->array [xs : (Mutable-Array ^a)]) : (Array ^a)
  (array-create xs))

(export array array-length array-ref append set array-empty? map filter fold mutable-array->array)
