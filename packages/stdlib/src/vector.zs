;; vector.zs — Vector operations via ImmutableArray<T>
(module vector)

;; CLR bindings (internal)
(import-clr
  System.Collections.Immutable
  [vector-length-raw System.Collections.Immutable.ImmutableArray.Length
    :instance-property : (Fn [(Vector ^a)] Int)]
  [vector-item-raw System.Collections.Immutable.ImmutableArray.Item
    :instance-indexer : (Fn [(Vector ^a) Int] ^a)]
  [vector-add-raw System.Collections.Immutable.ImmutableArray.Add
    :instance : (Fn [(Vector ^a) ^a] (Vector ^a))]
  [vector-set-raw System.Collections.Immutable.ImmutableArray.SetItem
    :instance : (Fn [(Vector ^a) Int ^a] (Vector ^a))])

;; Internal loop helpers (defined before the public functions that call them)

(define (vector/map-loop [xs : (Vector ^a)] [f : (Fn [^a] ^b)] [len : Int] [i : Int] [acc : (Vector ^b)]) : (Vector ^b)
  (if (= i len)
    acc
    (vector/map-loop xs f len (+ i 1) (vector-add-raw acc (f (vector-item-raw xs i))))))

(define (vector/filter-loop [xs : (Vector ^a)] [pred : (Fn [^a] Bool)] [len : Int] [i : Int] [acc : (Vector ^a)]) : (Vector ^a)
  (if (= i len)
    acc
    (let [item (vector-item-raw xs i)]
      (if (pred item)
        (vector/filter-loop xs pred len (+ i 1) (vector-add-raw acc item))
        (vector/filter-loop xs pred len (+ i 1) acc)))))

(define (vector/fold-loop [xs : (Vector ^a)] [f : (Fn [^b ^a] ^b)] [len : Int] [i : Int] [acc : ^b]) : ^b
  (if (= i len)
    acc
    (vector/fold-loop xs f len (+ i 1) (f acc (vector-item-raw xs i)))))

;; Exported functions

(define (vector/count [xs : (Vector ^a)]) : Int
  (vector-length-raw xs))

(define (vector/nth [xs : (Vector ^a)] [i : Int]) : ^a
  (vector-item-raw xs i))

(define (vector/append [xs : (Vector ^a)] [x : ^a]) : (Vector ^a)
  (vector-add-raw xs x))

(define (vector/set [xs : (Vector ^a)] [i : Int] [x : ^a]) : (Vector ^a)
  (vector-set-raw xs i x))

(define (vector/empty? [xs : (Vector ^a)]) : Bool
  (= (vector-length-raw xs) 0))

(define (vector/map [xs : (Vector ^a)] [f : (Fn [^a] ^b)]) : (Vector ^b)
  (let [len (vector-length-raw xs)]
    (vector/map-loop xs f len 0 (vector))))

(define (vector/filter [xs : (Vector ^a)] [pred : (Fn [^a] Bool)]) : (Vector ^a)
  (let [len (vector-length-raw xs)]
    (vector/filter-loop xs pred len 0 (vector))))

(define (vector/fold [xs : (Vector ^a)] [init : ^b] [f : (Fn [^b ^a] ^b)]) : ^b
  (let [len (vector-length-raw xs)]
    (vector/fold-loop xs f len 0 init)))

(export vector/count vector/nth vector/append vector/set vector/empty?
        vector/map vector/filter vector/fold)
