;; list.zs — List operations via ImmutableList<T>
(module list)

;; Pull in the Mutable-Array alias so variadic functions in this module can
;; resolve their synthesized rest-parameter type (Mutable-Array ^a).
(import stdlib/mutable/array)

;; Map the ZScheme name `List` to System.Collections.Immutable.ImmutableList<T> at codegen.
(define-type-alias (List ^a)
  System.Collections.Immutable.ImmutableList :from "System.Collections.Immutable")

;; CLR bindings (internal)
(import-clr
  System.Collections.Immutable
  [list-count-raw System.Collections.Immutable.ImmutableList.Count
    :instance-property : ((List ^a) -> Int)]
  [list-item-raw System.Collections.Immutable.ImmutableList.Item
    :instance-indexer : ((List ^a) Int -> ^a)]
  [list-add-raw System.Collections.Immutable.ImmutableList.Add
    :instance : ((List ^a) ^a -> (List ^a))]
  [list-insert-raw System.Collections.Immutable.ImmutableList.Insert
    :instance : ((List ^a) Int ^a -> (List ^a))]
  [list-remove-at-raw System.Collections.Immutable.ImmutableList.RemoveAt
    :instance : ((List ^a) Int -> (List ^a))]
  [list-add-range-raw System.Collections.Immutable.ImmutableList.AddRange
    :instance : ((List ^a) (List ^a) -> (List ^a))]
  [list-create System.Collections.Immutable.ImmutableList/Create ^a
    : ((Mutable-Array ^a) -> (List ^a))]
  [list-create-from-mutable System.Collections.Immutable.ImmutableList/CreateRange ^a
    : ((Mutable-List ^a) -> (List ^a))])

;; Constructor
(define (list [elements : ^a ...]) : (List ^a)
  (list-create elements))

;; Internal loop helpers (defined before the public functions that call them)

(define (list/map-loop [xs : (List ^a)] [f : (^a -> ^b)] [len : Int] [i : Int] [acc : (List ^b)]) : (List ^b)
  (if (= i len)
    acc
    (list/map-loop xs f len (+ i 1) (list-add-raw acc (f (list-item-raw xs i))))))

(define (list/filter-loop [xs : (List ^a)] [pred : (^a -> Bool)] [len : Int] [i : Int] [acc : (List ^a)]) : (List ^a)
  (if (= i len)
    acc
    (let [item (list-item-raw xs i)]
      (if (pred item)
        (list/filter-loop xs pred len (+ i 1) (list-add-raw acc item))
        (list/filter-loop xs pred len (+ i 1) acc)))))

(define (list/fold-loop [xs : (List ^a)] [f : (^b ^a -> ^b)] [len : Int] [i : Int] [acc : ^b]) : ^b
  (if (= i len)
    acc
    (list/fold-loop xs f len (+ i 1) (f acc (list-item-raw xs i)))))

;; Exported functions

(define (length [xs : (List ^a)]) : Int
  (list-count-raw xs))

(define (list-ref [xs : (List ^a)] [i : Int]) : ^a
  (list-item-raw xs i))

(define (list-head [xs : (List ^a)]) : ^a
  (list-item-raw xs 0))

(define (list-tail [xs : (List ^a)]) : (List ^a)
  (list-remove-at-raw xs 0))

(define (cons [x : ^a] [xs : (List ^a)]) : (List ^a)
  (list-insert-raw xs 0 x))

(define (car [xs : (List ^a)]) : ^a
  (list-item-raw xs 0))

(define (cdr [xs : (List ^a)]) : (List ^a)
  (list-remove-at-raw xs 0))

(define (append [xs : (List ^a)] [x : ^a]) : (List ^a)
  (list-add-raw xs x))

(define (concat [xs : (List ^a)] [ys : (List ^a)]) : (List ^a)
  (list-add-range-raw xs ys))

(define (empty? [xs : (List ^a)]) : Bool
  (= (list-count-raw xs) 0))

(define (map [xs : (List ^a)] [f : (^a -> ^b)]) : (List ^b)
  (let [len (list-count-raw xs)]
    (list/map-loop xs f len 0 (list))))

(define (filter [xs : (List ^a)] [pred : (^a -> Bool)]) : (List ^a)
  (let [len (list-count-raw xs)]
    (list/filter-loop xs pred len 0 (list))))

(define (fold [xs : (List ^a)] [init : ^b] [f : (^b ^a -> ^b)]) : ^b
  (let [len (list-count-raw xs)]
    (list/fold-loop xs f len 0 init)))

;; Conversions

;; Mutable-List -> List via ImmutableList.CreateRange<T>(IEnumerable<T>).
(define (mutable-list->list [xs : (Mutable-List ^a)]) : (List ^a)
  (list-create-from-mutable xs))

(export list length list-ref list-head list-tail cons car cdr append concat empty? map filter fold mutable-list->list)
