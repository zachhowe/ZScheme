;; list.zs — List operations via ImmutableList<T>
(module list)

;; CLR bindings (internal)
(import-clr
  System.Collections.Immutable
  [list-count-raw System.Collections.Immutable.ImmutableList.Count
    :instance-property : (Fn [(List ^a)] Int)]
  [list-item-raw System.Collections.Immutable.ImmutableList.Item
    :instance-indexer : (Fn [(List ^a) Int] ^a)]
  [list-add-raw System.Collections.Immutable.ImmutableList.Add
    :instance : (Fn [(List ^a) ^a] (List ^a))]
  [list-insert-raw System.Collections.Immutable.ImmutableList.Insert
    :instance : (Fn [(List ^a) Int ^a] (List ^a))]
  [list-remove-at-raw System.Collections.Immutable.ImmutableList.RemoveAt
    :instance : (Fn [(List ^a) Int] (List ^a))]
  [list-add-range-raw System.Collections.Immutable.ImmutableList.AddRange
    :instance : (Fn [(List ^a) (List ^a)] (List ^a))])

;; Internal loop helpers (defined before the public functions that call them)

(define (list--map-loop [xs : (List ^a)] [f : (Fn [^a] ^b)] [len : Int] [i : Int] [acc : (List ^b)]) : (List ^b)
  (if (= i len)
    acc
    (list--map-loop xs f len (+ i 1) (list-add-raw acc (f (list-item-raw xs i))))))

(define (list--filter-loop [xs : (List ^a)] [pred : (Fn [^a] Bool)] [len : Int] [i : Int] [acc : (List ^a)]) : (List ^a)
  (if (= i len)
    acc
    (let [item (list-item-raw xs i)]
      (if (pred item)
        (list--filter-loop xs pred len (+ i 1) (list-add-raw acc item))
        (list--filter-loop xs pred len (+ i 1) acc)))))

(define (list--fold-loop [xs : (List ^a)] [f : (Fn [^b ^a] ^b)] [len : Int] [i : Int] [acc : ^b]) : ^b
  (if (= i len)
    acc
    (list--fold-loop xs f len (+ i 1) (f acc (list-item-raw xs i)))))

;; Exported functions

(define (list/count [xs : (List ^a)]) : Int
  (list-count-raw xs))

(define (list/nth [xs : (List ^a)] [i : Int]) : ^a
  (list-item-raw xs i))

(define (list/head [xs : (List ^a)]) : ^a
  (list-item-raw xs 0))

(define (list/tail [xs : (List ^a)]) : (List ^a)
  (list-remove-at-raw xs 0))

(define (list/cons [x : ^a] [xs : (List ^a)]) : (List ^a)
  (list-insert-raw xs 0 x))

(define (list/append [xs : (List ^a)] [x : ^a]) : (List ^a)
  (list-add-raw xs x))

(define (list/concat [xs : (List ^a)] [ys : (List ^a)]) : (List ^a)
  (list-add-range-raw xs ys))

(define (list/empty? [xs : (List ^a)]) : Bool
  (= (list-count-raw xs) 0))

(define (list/map [xs : (List ^a)] [f : (Fn [^a] ^b)]) : (List ^b)
  (let [len (list-count-raw xs)]
    (list--map-loop xs f len 0 (list))))

(define (list/filter [xs : (List ^a)] [pred : (Fn [^a] Bool)]) : (List ^a)
  (let [len (list-count-raw xs)]
    (list--filter-loop xs pred len 0 (list))))

(define (list/fold [xs : (List ^a)] [init : ^b] [f : (Fn [^b ^a] ^b)]) : ^b
  (let [len (list-count-raw xs)]
    (list--fold-loop xs f len 0 init)))

(export list/count list/nth list/head list/tail list/cons list/append
        list/concat list/empty? list/map list/filter list/fold
        list--map-loop list--filter-loop list--fold-loop)
