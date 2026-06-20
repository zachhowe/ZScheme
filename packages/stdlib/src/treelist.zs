;; treelist.zs — TreeList operations via ImmutableList<T> (AVL-backed)
(module treelist)

;; Mutable-Vector is the synthesized rest-parameter type for variadic functions, and
;; doubles as a scratch buffer for treelist-sort. Vector supplies (Vector ^a) for the
;; (vector <-> treelist) conversions. Option carries treelist-find / treelist-index-of
;; results.
(import stdlib/mutable/vector)
(import stdlib/vector)
(import stdlib/option)

;; Map the ZScheme name `TreeList` to System.Collections.Immutable.ImmutableList<T> at codegen.
(define-type-alias (TreeList ^a)
  System.Collections.Immutable.ImmutableList :from "System.Collections.Immutable")

;; Mutable-TreeList is referenced below (treelist-copy) but its canonical declaration lives in
;; stdlib/mutable/treelist, which can't be imported here (mutual TreeList<->Mutable-TreeList
;; cycle). Re-declare it locally — must mirror the canonical target exactly.
(define-type-alias (Mutable-TreeList ^a) System.Collections.Generic.List)

;; CLR bindings (internal)
(import-clr
  System.Collections.Immutable
  System.Linq
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
  [treelist-set-raw System.Collections.Immutable.ImmutableList.SetItem
    :instance : ((TreeList ^a) Int ^a -> (TreeList ^a))]
  [treelist-add-range-raw System.Collections.Immutable.ImmutableList.AddRange
    :instance : ((TreeList ^a) (TreeList ^a) -> (TreeList ^a))]
  [treelist-get-range-raw System.Collections.Immutable.ImmutableList.GetRange
    :instance : ((TreeList ^a) Int Int -> (TreeList ^a))]
  [treelist-reverse-raw System.Collections.Immutable.ImmutableList.Reverse
    :instance : ((TreeList ^a) -> (TreeList ^a))]
  [treelist-contains-raw System.Collections.Immutable.ImmutableList.Contains
    :instance : ((TreeList ^a) ^a -> Bool)]
  [treelist-index-of-raw System.Collections.Immutable.ImmutableList.IndexOf
    :instance : ((TreeList ^a) ^a -> Int)]
  [treelist-create System.Collections.Immutable.ImmutableList/Create ^a
    : ((Mutable-Vector ^a) -> (TreeList ^a))]
  [treelist-create-from-mutable System.Collections.Immutable.ImmutableList/CreateRange ^a
    : ((Mutable-TreeList ^a) -> (TreeList ^a))]
  [treelist-to-array-raw System.Linq.Enumerable/ToArray ^a
    : ((TreeList ^a) -> (Mutable-Vector ^a))])

;; Constructors

(define (treelist [elements : ^a ...]) : (TreeList ^a)
  (treelist-create elements))

(define (treelist/make-loop [acc : (TreeList ^a)] [v : ^a] [n : Int] [i : Int]) : (TreeList ^a)
  (if (= i n)
    acc
    (treelist/make-loop (treelist-add-raw acc v) v n (+ i 1))))

(define (make-treelist [n : Int] [v : ^a]) : (TreeList ^a)
  (treelist/make-loop (treelist) v n 0))

;; Internal loop helpers

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

(define (treelist/for-each-loop [xs : (TreeList ^a)] [f : (^a -> Unit)] [len : Int] [i : Int]) : Unit
  (if (= i len)
    ()
    (begin
      (f (treelist-item-raw xs i))
      (treelist/for-each-loop xs f len (+ i 1)))))

(define (treelist/find-loop [xs : (TreeList ^a)] [pred : (^a -> Bool)] [len : Int] [i : Int]) : (Option ^a)
  (if (= i len)
    None
    (let [item (treelist-item-raw xs i)]
      (if (pred item)
        (Some item)
        (treelist/find-loop xs pred len (+ i 1))))))

(define (treelist/append-loop [tls : (Mutable-Vector (TreeList ^a))] [len : Int] [i : Int] [acc : (TreeList ^a)]) : (TreeList ^a)
  (if (= i len)
    acc
    (treelist/append-loop tls len (+ i 1) (treelist-add-range-raw acc (vector-ref tls i)))))

(define (treelist/append-star-loop [xs : (TreeList (TreeList ^a))] [len : Int] [i : Int] [acc : (TreeList ^a)]) : (TreeList ^a)
  (if (= i len)
    acc
    (treelist/append-star-loop xs len (+ i 1) (treelist-add-range-raw acc (treelist-item-raw xs i)))))

(define (treelist/from-vector-loop [xs : (Vector ^a)] [len : Int] [i : Int] [acc : (TreeList ^a)]) : (TreeList ^a)
  (if (= i len)
    acc
    (treelist/from-vector-loop xs len (+ i 1) (treelist-add-raw acc (vector-ref xs i)))))

(define (treelist/to-vector-loop [xs : (TreeList ^a)] [len : Int] [i : Int] [acc : (Vector ^a)]) : (Vector ^a)
  (if (= i len)
    acc
    (treelist/to-vector-loop xs len (+ i 1) (vector-append acc (vector (treelist-item-raw xs i))))))

;; Insertion sort over a mutable T[] buffer. O(n^2) but simple and avoids a
;; circular dependency on mutable/treelist for sort!.
(define (treelist/sort-shift! [arr : (Mutable-Vector ^a)] [less? : (^a ^a -> Bool)] [j : Int] [v : ^a]) : Unit
  (if (= j 0)
    (vector-set! arr 0 v)
    (let [prev (vector-ref arr (- j 1))]
      (if (less? v prev)
        (begin
          (vector-set! arr j prev)
          (treelist/sort-shift! arr less? (- j 1) v))
        (vector-set! arr j v)))))

(define (treelist/sort-loop! [arr : (Mutable-Vector ^a)] [less? : (^a ^a -> Bool)] [n : Int] [i : Int]) : Unit
  (if (>= i n)
    ()
    (begin
      (treelist/sort-shift! arr less? i (vector-ref arr i))
      (treelist/sort-loop! arr less? n (+ i 1)))))

;; Exported functions

(define (treelist-length [xs : (TreeList ^a)]) : Int
  (treelist-count-raw xs))

(define (treelist-ref [xs : (TreeList ^a)] [i : Int]) : ^a
  (treelist-item-raw xs i))

(define (treelist-first [xs : (TreeList ^a)]) : ^a
  (treelist-item-raw xs 0))

(define (treelist-last [xs : (TreeList ^a)]) : ^a
  (treelist-item-raw xs (- (treelist-count-raw xs) 1)))

(define (treelist-rest [xs : (TreeList ^a)]) : (TreeList ^a)
  (treelist-remove-at-raw xs 0))

(define (treelist-cons [x : ^a] [xs : (TreeList ^a)]) : (TreeList ^a)
  (treelist-insert-raw xs 0 x))

(define (treelist-add [xs : (TreeList ^a)] [x : ^a]) : (TreeList ^a)
  (treelist-add-raw xs x))

(define (treelist-insert [xs : (TreeList ^a)] [pos : Int] [v : ^a]) : (TreeList ^a)
  (treelist-insert-raw xs pos v))

(define (treelist-delete [xs : (TreeList ^a)] [pos : Int]) : (TreeList ^a)
  (treelist-remove-at-raw xs pos))

(define (treelist-set [xs : (TreeList ^a)] [pos : Int] [v : ^a]) : (TreeList ^a)
  (treelist-set-raw xs pos v))

(define (treelist-append [tls : (TreeList ^a) ...]) : (TreeList ^a)
  (treelist/append-loop tls (vector-length tls) 0 (treelist)))

(define (treelist-append* [xs : (TreeList (TreeList ^a))]) : (TreeList ^a)
  (treelist/append-star-loop xs (treelist-count-raw xs) 0 (treelist)))

(define (treelist-take [xs : (TreeList ^a)] [n : Int]) : (TreeList ^a)
  (treelist-get-range-raw xs 0 n))

(define (treelist-drop [xs : (TreeList ^a)] [n : Int]) : (TreeList ^a)
  (treelist-get-range-raw xs n (- (treelist-count-raw xs) n)))

(define (treelist-take-right [xs : (TreeList ^a)] [n : Int]) : (TreeList ^a)
  (let [len (treelist-count-raw xs)]
    (treelist-get-range-raw xs (- len n) n)))

(define (treelist-drop-right [xs : (TreeList ^a)] [n : Int]) : (TreeList ^a)
  (let [len (treelist-count-raw xs)]
    (treelist-get-range-raw xs 0 (- len n))))

(define (treelist-sublist [xs : (TreeList ^a)] [from : Int] [to : Int]) : (TreeList ^a)
  (treelist-get-range-raw xs from (- to from)))

(define (treelist-reverse [xs : (TreeList ^a)]) : (TreeList ^a)
  (treelist-reverse-raw xs))

(define (treelist-empty? [xs : (TreeList ^a)]) : Bool
  (= (treelist-count-raw xs) 0))

(define (treelist-member? [xs : (TreeList ^a)] [v : ^a]) : Bool
  (treelist-contains-raw xs v))

(define (treelist-index-of [xs : (TreeList ^a)] [v : ^a]) : (Option Int)
  (let [idx (treelist-index-of-raw xs v)]
    (if (= idx -1)
      None
      (Some idx))))

(define (treelist-find [xs : (TreeList ^a)] [pred : (^a -> Bool)]) : (Option ^a)
  (treelist/find-loop xs pred (treelist-count-raw xs) 0))

(define (treelist-map [xs : (TreeList ^a)] [f : (^a -> ^b)]) : (TreeList ^b)
  (let [len (treelist-count-raw xs)]
    (treelist/map-loop xs f len 0 (treelist))))

(define (treelist-filter [xs : (TreeList ^a)] [pred : (^a -> Bool)]) : (TreeList ^a)
  (let [len (treelist-count-raw xs)]
    (treelist/filter-loop xs pred len 0 (treelist))))

(define (treelist-fold [xs : (TreeList ^a)] [init : ^b] [f : (^b ^a -> ^b)]) : ^b
  (let [len (treelist-count-raw xs)]
    (treelist/fold-loop xs f len 0 init)))

(define (treelist-for-each [xs : (TreeList ^a)] [f : (^a -> Unit)]) : Unit
  (treelist/for-each-loop xs f (treelist-count-raw xs) 0))

(define (treelist-sort [xs : (TreeList ^a)] [less? : (^a ^a -> Bool)]) : (TreeList ^a)
  (let [arr (treelist-to-array-raw xs)]
    (begin
      (treelist/sort-loop! arr less? (vector-length arr) 1)
      (treelist-create arr))))

;; Conversions

;; Mutable-TreeList -> TreeList via ImmutableList.CreateRange<T>(IEnumerable<T>).
(define (mutable-treelist-snapshot [xs : (Mutable-TreeList ^a)]) : (TreeList ^a)
  (treelist-create-from-mutable xs))

(define (treelist->vector [xs : (TreeList ^a)]) : (Vector ^a)
  (treelist/to-vector-loop xs (treelist-count-raw xs) 0 (vector)))

(define (vector->treelist [xs : (Vector ^a)]) : (TreeList ^a)
  (treelist/from-vector-loop xs (vector-length xs) 0 (treelist)))

(export treelist make-treelist
        treelist-length treelist-ref treelist-first treelist-last treelist-rest
        treelist-cons treelist-add treelist-insert treelist-delete treelist-set
        treelist-append treelist-append*
        treelist-take treelist-drop treelist-take-right treelist-drop-right treelist-sublist
        treelist-reverse
        treelist-empty? treelist-member? treelist-index-of treelist-find
        treelist-map treelist-filter treelist-fold treelist-for-each
        treelist-sort
        mutable-treelist-snapshot
        treelist->vector vector->treelist)
