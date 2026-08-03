;; mutable-treelist.zs — Mutable-TreeList operations via List<T>
(module mutable-treelist)

;; Mutable-Vector supports the variadic rest-parameter type and reading variadic args;
;; Vector supports (vector <-> mutable-treelist) conversions; Option carries find /
;; index-of results.
(import stdlib/mutable/vector)
(import stdlib/vector)
(import stdlib/option)

;; Map the ZScheme name `Mutable-TreeList` to System.Collections.Generic.List<T> at codegen.
(define-type-alias (Mutable-TreeList ^a) System.Collections.Generic.List)

;; TreeList is referenced below (treelist-copy etc.) but its canonical declaration lives in
;; stdlib/treelist, which can't be imported here (mutual cycle). Re-declare it locally —
;; must mirror the canonical target exactly.
(define-type-alias (TreeList ^a)
  System.Collections.Immutable.ImmutableList :from "System.Collections.Immutable")

;; CLR bindings (internal)
(import-clr
  System.Collections.Generic
  System.Collections.Immutable
  System.Linq
  [ml-count-raw List.Count
    :instance-property : ((Mutable-TreeList ^a) -> Int)]
  [ml-item-raw List.Item
    :instance-indexer : ((Mutable-TreeList ^a) Int -> ^a)]
  [ml-set-item-raw List.Item
    :instance-indexer-set : ((Mutable-TreeList ^a) Int ^a -> Unit)]
  [ml-add-raw List.Add
    :instance : ((Mutable-TreeList ^a) ^a -> Unit)]
  [ml-insert-raw List.Insert
    :instance : ((Mutable-TreeList ^a) Int ^a -> Unit)]
  [ml-remove-at-raw List.RemoveAt
    :instance : ((Mutable-TreeList ^a) Int -> Unit)]
  [ml-clear-raw List.Clear
    :instance : ((Mutable-TreeList ^a) -> Unit)]
  [ml-contains-raw List.Contains
    :instance : ((Mutable-TreeList ^a) ^a -> Bool)]
  [ml-add-range-raw List.AddRange
    :instance : ((Mutable-TreeList ^a) (Mutable-TreeList ^a) -> Unit)]
  [ml-insert-range-raw List.InsertRange
    :instance : ((Mutable-TreeList ^a) Int (Mutable-TreeList ^a) -> Unit)]
  [ml-remove-range-raw List.RemoveRange
    :instance : ((Mutable-TreeList ^a) Int Int -> Unit)]
  [ml-get-range-raw List.GetRange
    :instance : ((Mutable-TreeList ^a) Int Int -> (Mutable-TreeList ^a))]
  [ml-reverse-raw List.Reverse
    :instance : ((Mutable-TreeList ^a) -> Unit)]
  [ml-index-of-raw List.IndexOf
    :instance : ((Mutable-TreeList ^a) ^a -> Int)]
  [treelist-to-mutable-raw Enumerable/ToList ^a
    : ((TreeList ^a) -> (Mutable-TreeList ^a))]
  [ml-from-mutable-raw Enumerable/ToList ^a
    : ((Mutable-TreeList ^a) -> (Mutable-TreeList ^a))]
  [ml-from-mutable-vector-raw Enumerable/ToList ^a
    : ((Mutable-Vector ^a) -> (Mutable-TreeList ^a))]
  [ml-from-vector-raw Enumerable/ToList ^a
    : ((Vector ^a) -> (Mutable-TreeList ^a))]
  [ml-snapshot-range-raw ImmutableList/CreateRange ^a
    : ((Mutable-TreeList ^a) -> (TreeList ^a))])

;; Every loop in this module serves exactly one public function, so each is defined inside its
;; caller and captures that function's arguments instead of threading them through every recursive
;; call. A nested define is lifted to a top-level static with its captures as leading parameters,
;; and a tail call to it still becomes a loop, so this costs nothing at runtime.

;; Constructors

(define (mutable-treelist [elements : ^a ...]) : (Mutable-TreeList ^a)
  (ml-from-mutable-vector-raw elements))

(define (make-mutable-treelist [n : Int] [v : ^a]) : (Mutable-TreeList ^a)
  (let ([xs (mutable-treelist)])
    (define (fill! [i : Int]) : Unit
      (if (= i n)
        ()
        (begin
          (ml-add-raw xs v)
          (fill! (+ i 1)))))
    (fill! 0)
    xs))

;; Exported functions

(define (mutable-treelist-length [xs : (Mutable-TreeList ^a)]) : Int
  (ml-count-raw xs))

(define (mutable-treelist-ref [xs : (Mutable-TreeList ^a)] [i : Int]) : ^a
  (ml-item-raw xs i))

(define (mutable-treelist-first [xs : (Mutable-TreeList ^a)]) : ^a
  (ml-item-raw xs 0))

(define (mutable-treelist-last [xs : (Mutable-TreeList ^a)]) : ^a
  (ml-item-raw xs (- (ml-count-raw xs) 1)))

(define (mutable-treelist-set! [xs : (Mutable-TreeList ^a)] [i : Int] [val : ^a]) : Unit
  (ml-set-item-raw xs i val))

(define (mutable-treelist-add! [xs : (Mutable-TreeList ^a)] [val : ^a]) : Unit
  (ml-add-raw xs val))

(define (mutable-treelist-cons! [val : ^a] [xs : (Mutable-TreeList ^a)]) : Unit
  (ml-insert-raw xs 0 val))

(define (mutable-treelist-insert! [xs : (Mutable-TreeList ^a)] [i : Int] [val : ^a]) : Unit
  (ml-insert-raw xs i val))

(define (mutable-treelist-delete! [xs : (Mutable-TreeList ^a)] [i : Int]) : Unit
  (ml-remove-at-raw xs i))

(define (mutable-treelist-clear! [xs : (Mutable-TreeList ^a)]) : Unit
  (ml-clear-raw xs))

(define (mutable-treelist-append! [xs : (Mutable-TreeList ^a)] [ys : (Mutable-TreeList ^a)]) : Unit
  (ml-add-range-raw xs ys))

(define (mutable-treelist-prepend! [xs : (Mutable-TreeList ^a)] [ys : (Mutable-TreeList ^a)]) : Unit
  (ml-insert-range-raw xs 0 ys))

(define (mutable-treelist-take! [xs : (Mutable-TreeList ^a)] [n : Int]) : Unit
  (let ([len (ml-count-raw xs)])
    (ml-remove-range-raw xs n (- len n))))

(define (mutable-treelist-drop! [xs : (Mutable-TreeList ^a)] [n : Int]) : Unit
  (ml-remove-range-raw xs 0 n))

(define (mutable-treelist-take-right! [xs : (Mutable-TreeList ^a)] [n : Int]) : Unit
  (let ([len (ml-count-raw xs)])
    (ml-remove-range-raw xs 0 (- len n))))

(define (mutable-treelist-drop-right! [xs : (Mutable-TreeList ^a)] [n : Int]) : Unit
  (let ([len (ml-count-raw xs)])
    (ml-remove-range-raw xs (- len n) n)))

(define (mutable-treelist-sublist! [xs : (Mutable-TreeList ^a)] [from : Int] [to : Int]) : Unit
  (let ([len (ml-count-raw xs)])
    (ml-remove-range-raw xs to (- len to))
    (ml-remove-range-raw xs 0 from)))

(define (mutable-treelist-reverse! [xs : (Mutable-TreeList ^a)]) : Unit
  (ml-reverse-raw xs))

(define (mutable-treelist-member? [xs : (Mutable-TreeList ^a)] [val : ^a]) : Bool
  (ml-contains-raw xs val))

(define (mutable-treelist-index-of [xs : (Mutable-TreeList ^a)] [val : ^a]) : (Option Int)
  (let ([idx (ml-index-of-raw xs val)])
    (if (= idx -1)
      None
      (Some idx))))

(define (mutable-treelist-find [xs : (Mutable-TreeList ^a)] [pred : (^a -> Bool)]) : (Option ^a)
  (let ([len (ml-count-raw xs)])
    (define (loop [i : Int]) : (Option ^a)
      (if (= i len)
        None
        (let ([item (ml-item-raw xs i)])
          (if (pred item)
            (Some item)
            (loop (+ i 1))))))
    (loop 0)))

(define (mutable-treelist-empty? [xs : (Mutable-TreeList ^a)]) : Bool
  (= (ml-count-raw xs) 0))

(define (mutable-treelist-map! [xs : (Mutable-TreeList ^a)] [f : (^a -> ^a)]) : Unit
  (let ([len (ml-count-raw xs)])
    (define (loop! [i : Int]) : Unit
      (if (= i len)
        ()
        (begin
          (ml-set-item-raw xs i (f (ml-item-raw xs i)))
          (loop! (+ i 1)))))
    (loop! 0)))

(define (mutable-treelist-for-each [xs : (Mutable-TreeList ^a)] [f : (^a -> Unit)]) : Unit
  (let ([len (ml-count-raw xs)])
    (define (loop [i : Int]) : Unit
      (if (= i len)
        ()
        (begin
          (f (ml-item-raw xs i))
          (loop (+ i 1)))))
    (loop 0)))

;; In-place insertion sort using the supplied less-than predicate. shift! walks an element down
;; into place, sort-loop! drives it across the list; both capture xs and less?.
(define (mutable-treelist-sort! [xs : (Mutable-TreeList ^a)] [less? : (^a ^a -> Bool)]) : Unit
  (let ([n (ml-count-raw xs)])
    (define (shift! [j : Int] [v : ^a]) : Unit
      (if (= j 0)
        (ml-set-item-raw xs 0 v)
        (let ([prev (ml-item-raw xs (- j 1))])
          (if (less? v prev)
            (begin
              (ml-set-item-raw xs j prev)
              (shift! (- j 1) v))
            (ml-set-item-raw xs j v)))))
    (define (sort-loop! [i : Int]) : Unit
      (if (>= i n)
        ()
        (begin
          (shift! i (ml-item-raw xs i))
          (sort-loop! (+ i 1)))))
    (sort-loop! 1)))

;; Conversions

;; TreeList -> Mutable-TreeList via Enumerable.ToList<T>.
(define (treelist-copy [xs : (TreeList ^a)]) : (Mutable-TreeList ^a)
  (treelist-to-mutable-raw xs))

;; Mutable-TreeList -> Mutable-TreeList (shallow copy) via Enumerable.ToList<T>.
(define (mutable-treelist-copy [xs : (Mutable-TreeList ^a)]) : (Mutable-TreeList ^a)
  (ml-from-mutable-raw xs))

;; Mutable-TreeList -> TreeList of a sub-range.
(define (mutable-treelist-snapshot/range [xs : (Mutable-TreeList ^a)] [from : Int] [to : Int]) : (TreeList ^a)
  (ml-snapshot-range-raw (ml-get-range-raw xs from (- to from))))

(define (mutable-treelist->vector [xs : (Mutable-TreeList ^a)]) : (Vector ^a)
  (let ([len (ml-count-raw xs)])
    (define (loop [i : Int] [acc : (Vector ^a)]) : (Vector ^a)
      (if (= i len)
        acc
        (loop (+ i 1) (vector-append acc (vector (ml-item-raw xs i))))))
    (loop 0 (vector))))

(define (vector->mutable-treelist [xs : (Vector ^a)]) : (Mutable-TreeList ^a)
  (ml-from-vector-raw xs))

(export mutable-treelist make-mutable-treelist
        mutable-treelist-length mutable-treelist-ref
        mutable-treelist-first mutable-treelist-last
        mutable-treelist-set! mutable-treelist-add! mutable-treelist-cons!
        mutable-treelist-insert! mutable-treelist-delete! mutable-treelist-clear!
        mutable-treelist-append! mutable-treelist-prepend!
        mutable-treelist-take! mutable-treelist-drop!
        mutable-treelist-take-right! mutable-treelist-drop-right!
        mutable-treelist-sublist! mutable-treelist-reverse!
        mutable-treelist-member? mutable-treelist-index-of mutable-treelist-find
        mutable-treelist-empty?
        mutable-treelist-map! mutable-treelist-for-each
        mutable-treelist-sort!
        treelist-copy mutable-treelist-copy mutable-treelist-snapshot/range
        mutable-treelist->vector vector->mutable-treelist)
