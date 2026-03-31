;; array.zs — Array operations via ImmutableArray<T>
(module array)

;; CLR bindings (internal)
(import-clr
  System.Collections.Immutable
  [array-length-raw System.Collections.Immutable.ImmutableArray.Length
    :instance-property : (Fn [(Array ^a)] Int)]
  [array-item-raw System.Collections.Immutable.ImmutableArray.Item
    :instance-indexer : (Fn [(Array ^a) Int] ^a)]
  [array-add-raw System.Collections.Immutable.ImmutableArray.Add
    :instance : (Fn [(Array ^a) ^a] (Array ^a))]
  [array-set-raw System.Collections.Immutable.ImmutableArray.SetItem
    :instance : (Fn [(Array ^a) Int ^a] (Array ^a))])

;; Internal loop helpers (defined before the public functions that call them)

(define (array/map-loop [xs : (Array ^a)] [f : (Fn [^a] ^b)] [len : Int] [i : Int] [acc : (Array ^b)]) : (Array ^b)
  (if (= i len)
    acc
    (array/map-loop xs f len (+ i 1) (array-add-raw acc (f (array-item-raw xs i))))))

(define (array/filter-loop [xs : (Array ^a)] [pred : (Fn [^a] Bool)] [len : Int] [i : Int] [acc : (Array ^a)]) : (Array ^a)
  (if (= i len)
    acc
    (let [item (array-item-raw xs i)]
      (if (pred item)
        (array/filter-loop xs pred len (+ i 1) (array-add-raw acc item))
        (array/filter-loop xs pred len (+ i 1) acc)))))

(define (array/fold-loop [xs : (Array ^a)] [f : (Fn [^b ^a] ^b)] [len : Int] [i : Int] [acc : ^b]) : ^b
  (if (= i len)
    acc
    (array/fold-loop xs f len (+ i 1) (f acc (array-item-raw xs i)))))

;; Exported functions

(define (array/count [xs : (Array ^a)]) : Int
  (array-length-raw xs))

(define (array/nth [xs : (Array ^a)] [i : Int]) : ^a
  (array-item-raw xs i))

(define (array/append [xs : (Array ^a)] [x : ^a]) : (Array ^a)
  (array-add-raw xs x))

(define (array/set [xs : (Array ^a)] [i : Int] [x : ^a]) : (Array ^a)
  (array-set-raw xs i x))

(define (array/empty? [xs : (Array ^a)]) : Bool
  (= (array-length-raw xs) 0))

(define (array/map [xs : (Array ^a)] [f : (Fn [^a] ^b)]) : (Array ^b)
  (let [len (array-length-raw xs)]
    (array/map-loop xs f len 0 (array))))

(define (array/filter [xs : (Array ^a)] [pred : (Fn [^a] Bool)]) : (Array ^a)
  (let [len (array-length-raw xs)]
    (array/filter-loop xs pred len 0 (array))))

(define (array/fold [xs : (Array ^a)] [init : ^b] [f : (Fn [^b ^a] ^b)]) : ^b
  (let [len (array-length-raw xs)]
    (array/fold-loop xs f len 0 init)))

(export array/count array/nth array/append array/set array/empty?
        array/map array/filter array/fold)
