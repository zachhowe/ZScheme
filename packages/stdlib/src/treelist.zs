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
  [treelist-count-raw ImmutableList.Count
    :instance-property : ((TreeList ^a) -> Int)]
  [treelist-item-raw ImmutableList.Item
    :instance-indexer : ((TreeList ^a) Int -> ^a)]
  [treelist-add-raw ImmutableList.Add
    :instance : ((TreeList ^a) ^a -> (TreeList ^a))]
  [treelist-insert-raw ImmutableList.Insert
    :instance : ((TreeList ^a) Int ^a -> (TreeList ^a))]
  [treelist-remove-at-raw ImmutableList.RemoveAt
    :instance : ((TreeList ^a) Int -> (TreeList ^a))]
  [treelist-set-raw ImmutableList.SetItem
    :instance : ((TreeList ^a) Int ^a -> (TreeList ^a))]
  [treelist-add-range-raw ImmutableList.AddRange
    :instance : ((TreeList ^a) (TreeList ^a) -> (TreeList ^a))]
  [treelist-get-range-raw ImmutableList.GetRange
    :instance : ((TreeList ^a) Int Int -> (TreeList ^a))]
  [treelist-reverse-raw ImmutableList.Reverse
    :instance : ((TreeList ^a) -> (TreeList ^a))]
  [treelist-contains-raw ImmutableList.Contains
    :instance : ((TreeList ^a) ^a -> Bool)]
  [treelist-index-of-raw ImmutableList.IndexOf
    :instance : ((TreeList ^a) ^a -> Int)]
  [treelist-create ImmutableList/Create ^a
    : ((Mutable-Vector ^a) -> (TreeList ^a))]
  [treelist-create-from-mutable ImmutableList/CreateRange ^a
    : ((Mutable-TreeList ^a) -> (TreeList ^a))]
  [treelist-to-array-raw Enumerable/ToArray ^a
    : ((TreeList ^a) -> (Mutable-Vector ^a))])

;; Constructors

(define (treelist [elements : ^a ...]) : (TreeList ^a)
  (treelist-create elements))

(define (make-treelist [n : Int] [v : ^a]) : (TreeList ^a)
  (define (loop [i : Int] [acc : (TreeList ^a)]) : (TreeList ^a)
    (if (= i n)
      acc
      (loop (+ i 1) (treelist-add-raw acc v))))
  (loop 0 (treelist)))

;; Every loop below serves exactly one public function, so each is defined inside its caller and
;; captures that function's arguments — and the hoisted length — instead of threading them through
;; every recursive call. A nested define is lifted to a top-level static with its captures as
;; leading parameters, and a tail call to it still becomes a loop, so this costs nothing at runtime.

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
  (let ([len (vector-length tls)])
    (define (loop [i : Int] [acc : (TreeList ^a)]) : (TreeList ^a)
      (if (= i len)
        acc
        (loop (+ i 1) (treelist-add-range-raw acc (vector-ref tls i)))))
    (loop 0 (treelist))))

(define (treelist-append* [xs : (TreeList (TreeList ^a))]) : (TreeList ^a)
  (let ([len (treelist-count-raw xs)])
    (define (loop [i : Int] [acc : (TreeList ^a)]) : (TreeList ^a)
      (if (= i len)
        acc
        (loop (+ i 1) (treelist-add-range-raw acc (treelist-item-raw xs i)))))
    (loop 0 (treelist))))

(define (treelist-take [xs : (TreeList ^a)] [n : Int]) : (TreeList ^a)
  (treelist-get-range-raw xs 0 n))

(define (treelist-drop [xs : (TreeList ^a)] [n : Int]) : (TreeList ^a)
  (treelist-get-range-raw xs n (- (treelist-count-raw xs) n)))

(define (treelist-take-right [xs : (TreeList ^a)] [n : Int]) : (TreeList ^a)
  (let ([len (treelist-count-raw xs)])
    (treelist-get-range-raw xs (- len n) n)))

(define (treelist-drop-right [xs : (TreeList ^a)] [n : Int]) : (TreeList ^a)
  (let ([len (treelist-count-raw xs)])
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
  (let ([idx (treelist-index-of-raw xs v)])
    (if (= idx -1)
      None
      (Some idx))))

(define (treelist-find [xs : (TreeList ^a)] [pred : (^a -> Bool)]) : (Option ^a)
  (let ([len (treelist-count-raw xs)])
    (define (loop [i : Int]) : (Option ^a)
      (if (= i len)
        None
        (let ([item (treelist-item-raw xs i)])
          (if (pred item)
            (Some item)
            (loop (+ i 1))))))
    (loop 0)))

(define (treelist-map [xs : (TreeList ^a)] [f : (^a -> ^b)]) : (TreeList ^b)
  (let ([len (treelist-count-raw xs)])
    (define (loop [i : Int] [acc : (TreeList ^b)]) : (TreeList ^b)
      (if (= i len)
        acc
        (loop (+ i 1) (treelist-add-raw acc (f (treelist-item-raw xs i))))))
    (loop 0 (treelist))))

(define (treelist-filter [xs : (TreeList ^a)] [pred : (^a -> Bool)]) : (TreeList ^a)
  (let ([len (treelist-count-raw xs)])
    (define (loop [i : Int] [acc : (TreeList ^a)]) : (TreeList ^a)
      (if (= i len)
        acc
        (let ([item (treelist-item-raw xs i)])
          (if (pred item)
            (loop (+ i 1) (treelist-add-raw acc item))
            (loop (+ i 1) acc)))))
    (loop 0 (treelist))))

(define (treelist-fold [xs : (TreeList ^a)] [init : ^b] [f : (^b ^a -> ^b)]) : ^b
  (let ([len (treelist-count-raw xs)])
    (define (loop [i : Int] [acc : ^b]) : ^b
      (if (= i len)
        acc
        (loop (+ i 1) (f acc (treelist-item-raw xs i)))))
    (loop 0 init)))

(define (treelist-for-each [xs : (TreeList ^a)] [f : (^a -> Unit)]) : Unit
  (let ([len (treelist-count-raw xs)])
    (define (loop [i : Int]) : Unit
      (if (= i len)
        ()
        (begin
          (f (treelist-item-raw xs i))
          (loop (+ i 1)))))
    (loop 0)))

;; Insertion sort over a mutable T[] buffer. O(n^2) but simple, and it avoids a circular dependency
;; on mutable/treelist for sort!. shift! walks an element down into place, sort-loop! drives it
;; across the buffer; both capture arr and less?.
(define (treelist-sort [xs : (TreeList ^a)] [less? : (^a ^a -> Bool)]) : (TreeList ^a)
  (let ([arr (treelist-to-array-raw xs)])
    (define n (vector-length arr))
    (define (shift! [j : Int] [v : ^a]) : Unit
      (if (= j 0)
        (vector-set! arr 0 v)
        (let ([prev (vector-ref arr (- j 1))])
          (if (less? v prev)
            (begin
              (vector-set! arr j prev)
              (shift! (- j 1) v))
            (vector-set! arr j v)))))
    (define (sort-loop! [i : Int]) : Unit
      (if (>= i n)
        ()
        (begin
          (shift! i (vector-ref arr i))
          (sort-loop! (+ i 1)))))
    (sort-loop! 1)
    (treelist-create arr)))

;; Conversions

;; Mutable-TreeList -> TreeList via ImmutableList.CreateRange<T>(IEnumerable<T>).
(define (mutable-treelist-snapshot [xs : (Mutable-TreeList ^a)]) : (TreeList ^a)
  (treelist-create-from-mutable xs))

(define (treelist->vector [xs : (TreeList ^a)]) : (Vector ^a)
  (let ([len (treelist-count-raw xs)])
    (define (loop [i : Int] [acc : (Vector ^a)]) : (Vector ^a)
      (if (= i len)
        acc
        (loop (+ i 1) (vector-append acc (vector (treelist-item-raw xs i))))))
    (loop 0 (vector))))

(define (vector->treelist [xs : (Vector ^a)]) : (TreeList ^a)
  (let ([len (vector-length xs)])
    (define (loop [i : Int] [acc : (TreeList ^a)]) : (TreeList ^a)
      (if (= i len)
        acc
        (loop (+ i 1) (treelist-add-raw acc (vector-ref xs i)))))
    (loop 0 (treelist))))

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
