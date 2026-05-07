;; treelist.zs — TreeList operations via ImmutableList<T> (AVL-backed)
(module treelist)

;; Pull in the Mutable-Array alias so variadic functions in this module can
;; resolve their synthesized rest-parameter type (Mutable-Array ^a).
(import stdlib/mutable/array)

;; Map the ZScheme name `TreeList` to System.Collections.Immutable.ImmutableList<T> at codegen.
(define-type-alias (TreeList ^a)
  System.Collections.Immutable.ImmutableList :from "System.Collections.Immutable")

;; CLR bindings (internal)
(import-clr
  System.Collections.Immutable
  [treelist-count-raw System.Collections.Immutable.ImmutableList.Count
    :instance-property : ((TreeList ^a) -> Int)]
  [treelist-item-raw System.Collections.Immutable.ImmutableList.Item
    :instance-indexer : ((TreeList ^a) Int -> ^a)]
  [treelist-add-raw System.Collections.Immutable.ImmutableList.Add
    :instance : ((TreeList ^a) ^a -> (TreeList ^a))]
  [treelist-insert-raw System.Collections.Immutable.ImmutableList.Insert
    :instance : ((TreeList ^a) Int ^a -> (TreeList ^a))]
  [treelist-remove-at-raw System.Collections.Immutable.ImmutableList.RemoveAt
    :instance : ((TreeList ^a) Int -> (TreeList ^a))]
  [treelist-add-range-raw System.Collections.Immutable.ImmutableList.AddRange
    :instance : ((TreeList ^a) (TreeList ^a) -> (TreeList ^a))]
  [treelist-create System.Collections.Immutable.ImmutableList/Create ^a
    : ((Mutable-Array ^a) -> (TreeList ^a))]
  [treelist-create-from-mutable System.Collections.Immutable.ImmutableList/CreateRange ^a
    : ((Mutable-TreeList ^a) -> (TreeList ^a))])

;; Constructor
(define (treelist [elements : ^a ...]) : (TreeList ^a)
  (treelist-create elements))

;; Internal loop helpers (defined before the public functions that call them)

(define (treelist/map-loop [xs : (TreeList ^a)] [f : (^a -> ^b)] [len : Int] [i : Int] [acc : (TreeList ^b)]) : (TreeList ^b)
  (if (= i len)
    acc
    (treelist/map-loop xs f len (+ i 1) (treelist-add-raw acc (f (treelist-item-raw xs i))))))

(define (treelist/filter-loop [xs : (TreeList ^a)] [pred : (^a -> Bool)] [len : Int] [i : Int] [acc : (TreeList ^a)]) : (TreeList ^a)
  (if (= i len)
    acc
    (let [item (treelist-item-raw xs i)]
      (if (pred item)
        (treelist/filter-loop xs pred len (+ i 1) (treelist-add-raw acc item))
        (treelist/filter-loop xs pred len (+ i 1) acc)))))

(define (treelist/fold-loop [xs : (TreeList ^a)] [f : (^b ^a -> ^b)] [len : Int] [i : Int] [acc : ^b]) : ^b
  (if (= i len)
    acc
    (treelist/fold-loop xs f len (+ i 1) (f acc (treelist-item-raw xs i)))))

;; Exported functions

(define (length [xs : (TreeList ^a)]) : Int
  (treelist-count-raw xs))

(define (list-ref [xs : (TreeList ^a)] [i : Int]) : ^a
  (treelist-item-raw xs i))

(define (list-head [xs : (TreeList ^a)]) : ^a
  (treelist-item-raw xs 0))

(define (list-tail [xs : (TreeList ^a)]) : (TreeList ^a)
  (treelist-remove-at-raw xs 0))

(define (cons [x : ^a] [xs : (TreeList ^a)]) : (TreeList ^a)
  (treelist-insert-raw xs 0 x))

(define (car [xs : (TreeList ^a)]) : ^a
  (treelist-item-raw xs 0))

(define (cdr [xs : (TreeList ^a)]) : (TreeList ^a)
  (treelist-remove-at-raw xs 0))

(define (append [xs : (TreeList ^a)] [x : ^a]) : (TreeList ^a)
  (treelist-add-raw xs x))

(define (concat [xs : (TreeList ^a)] [ys : (TreeList ^a)]) : (TreeList ^a)
  (treelist-add-range-raw xs ys))

(define (empty? [xs : (TreeList ^a)]) : Bool
  (= (treelist-count-raw xs) 0))

(define (map [xs : (TreeList ^a)] [f : (^a -> ^b)]) : (TreeList ^b)
  (let [len (treelist-count-raw xs)]
    (treelist/map-loop xs f len 0 (treelist))))

(define (filter [xs : (TreeList ^a)] [pred : (^a -> Bool)]) : (TreeList ^a)
  (let [len (treelist-count-raw xs)]
    (treelist/filter-loop xs pred len 0 (treelist))))

(define (fold [xs : (TreeList ^a)] [init : ^b] [f : (^b ^a -> ^b)]) : ^b
  (let [len (treelist-count-raw xs)]
    (treelist/fold-loop xs f len 0 init)))

;; Conversions

;; Mutable-TreeList -> TreeList via ImmutableList.CreateRange<T>(IEnumerable<T>).
(define (mutable-treelist->treelist [xs : (Mutable-TreeList ^a)]) : (TreeList ^a)
  (treelist-create-from-mutable xs))

(export treelist length list-ref list-head list-tail cons car cdr append concat empty? map filter fold mutable-treelist->treelist)
