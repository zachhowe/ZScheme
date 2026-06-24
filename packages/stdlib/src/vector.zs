;; vector.zs — Vector operations via ImmutableArray<T>
(module vector)

(import stdlib/option)
;; Pull in the Mutable-Vector alias so variadic functions in this module can
;; resolve their synthesized rest-parameter type (Mutable-Vector ^a).
(import stdlib/mutable/vector)

;; Map the ZScheme name `Vector` to System.Collections.Immutable.ImmutableArray<T> at codegen.
(define-type-alias (Vector ^a)
  System.Collections.Immutable.ImmutableArray :from "System.Collections.Immutable")

;; CLR bindings (internal)
(import-clr
  System.Collections.Immutable
  System.Linq
  [vector-length-raw System.Collections.Immutable.ImmutableArray.Length
    :instance-property : ((Vector ^a) -> Int)]
  [vector-item-raw System.Collections.Immutable.ImmutableArray.Item
    :instance-indexer : ((Vector ^a) Int -> ^a)]
  [vector-add-raw System.Collections.Immutable.ImmutableArray.Add
    :instance : ((Vector ^a) ^a -> (Vector ^a))]
  [vector-add-range-raw System.Collections.Immutable.ImmutableArray.AddRange
    :instance : ((Vector ^a) (Vector ^a) -> (Vector ^a))]
  [vector-set-raw System.Collections.Immutable.ImmutableArray.SetItem
    :instance : ((Vector ^a) Int ^a -> (Vector ^a))]
  [vector-index-of-raw System.Collections.Immutable.ImmutableArray.IndexOf
    :instance : ((Vector ^a) ^a -> Int)]
  [vector-create System.Collections.Immutable.ImmutableArray/Create ^a
    : ((Mutable-Vector ^a) -> (Vector ^a))])

;; Constructors

(define (vector [elements : ^a ...]) : (Vector ^a)
  (vector-create elements))

;; Internal loop helpers

(define (vector/build-loop [f : (Int -> ^a)] [n : Int] [i : Int] [acc : (Vector ^a)]) : (Vector ^a)
  (if (= i n)
    acc
    (vector/build-loop f n (+ i 1) (vector-add-raw acc (f i)))))

(define (vector/fill-loop [v : ^a] [n : Int] [i : Int] [acc : (Vector ^a)]) : (Vector ^a)
  (if (= i n)
    acc
    (vector/fill-loop v n (+ i 1) (vector-add-raw acc v))))

(define (vector/map-loop [xs : (Vector ^a)] [f : (^a -> ^b)] [len : Int] [i : Int] [acc : (Vector ^b)]) : (Vector ^b)
  (if (= i len)
    acc
    (vector/map-loop xs f len (+ i 1) (vector-add-raw acc (f (vector-item-raw xs i))))))

(define (vector/filter-loop [xs : (Vector ^a)] [pred : (^a -> Bool)] [keep? : Bool] [len : Int] [i : Int] [acc : (Vector ^a)]) : (Vector ^a)
  (if (= i len)
    acc
    (let ([item (vector-item-raw xs i)])
      (if (= (pred item) keep?)
        (vector/filter-loop xs pred keep? len (+ i 1) (vector-add-raw acc item))
        (vector/filter-loop xs pred keep? len (+ i 1) acc)))))

(define (vector/fold-loop [xs : (Vector ^a)] [f : (^b ^a -> ^b)] [len : Int] [i : Int] [acc : ^b]) : ^b
  (if (= i len)
    acc
    (vector/fold-loop xs f len (+ i 1) (f acc (vector-item-raw xs i)))))

(define (vector/append-loop [vs : (Mutable-Vector (Vector ^a))] [n : Int] [i : Int] [acc : (Vector ^a)]) : (Vector ^a)
  (if (= i n)
    acc
    (vector/append-loop vs n (+ i 1) (vector-add-range-raw acc (vector-ref vs i)))))

(define (vector/copy-loop [xs : (Vector ^a)] [end : Int] [i : Int] [acc : (Vector ^a)]) : (Vector ^a)
  (if (= i end)
    acc
    (vector/copy-loop xs end (+ i 1) (vector-add-raw acc (vector-item-raw xs i)))))

(define (vector/count-loop [xs : (Vector ^a)] [pred : (^a -> Bool)] [len : Int] [i : Int] [acc : Int]) : Int
  (if (= i len)
    acc
    (vector/count-loop xs pred len (+ i 1)
      (if (pred (vector-item-raw xs i)) (+ acc 1) acc))))

(define (vector/arg-loop [xs : (Vector ^a)] [f : (^a -> Int)] [less? : (Int Int -> Bool)] [len : Int] [i : Int] [best-idx : Int] [best-key : Int]) : ^a
  (if (= i len)
    (vector-item-raw xs best-idx)
    (let ([k (f (vector-item-raw xs i))])
      (if (less? k best-key)
        (vector/arg-loop xs f less? len (+ i 1) i k)
        (vector/arg-loop xs f less? len (+ i 1) best-idx best-key)))))

;; In-place insertion sort over a Mutable-Vector buffer, used by vector-sort.
(define (vector/sort-shift! [arr : (Mutable-Vector ^a)] [less? : (^a ^a -> Bool)] [j : Int] [v : ^a]) : Unit
  (if (= j 0)
    (vector-set! arr 0 v)
    (let ([prev (vector-ref arr (- j 1))])
      (if (less? v prev)
        (begin
          (vector-set! arr j prev)
          (vector/sort-shift! arr less? (- j 1) v))
        (vector-set! arr j v)))))

(define (vector/sort-loop! [arr : (Mutable-Vector ^a)] [less? : (^a ^a -> Bool)] [n : Int] [i : Int]) : Unit
  (if (>= i n)
    ()
    (begin
      (vector/sort-shift! arr less? i (vector-ref arr i))
      (vector/sort-loop! arr less? n (+ i 1)))))

;; (build-vector n proc) — element i = (proc i) for i in [0, n).
(define (build-vector [n : Int] [f : (Int -> ^a)]) : (Vector ^a)
  (vector/build-loop f n 0 (vector)))

;; (make-vector n v) — length-n vector filled with v.
(define (make-vector [n : Int] [v : ^a]) : (Vector ^a)
  (vector/fill-loop v n 0 (vector)))

;; Length and access

(define (vector-length [xs : (Vector ^a)]) : Int
  (vector-length-raw xs))

(define (vector-ref [xs : (Vector ^a)] [i : Int]) : ^a
  (vector-item-raw xs i))

(define (vector-empty? [xs : (Vector ^a)]) : Bool
  (= (vector-length-raw xs) 0))

;; Functional update

;; (vector-set/copy v i x) — return a new vector with index i set to x.
(define (vector-set/copy [xs : (Vector ^a)] [i : Int] [x : ^a]) : (Vector ^a)
  (vector-set-raw xs i x))

;; (vector-append v ...) — concatenate any number of vectors.
(define (vector-append [vs : (Vector ^a) ...]) : (Vector ^a)
  (vector/append-loop vs (vector-length vs) 0 (vector)))

;; Iteration / transformation

(define (vector-map [xs : (Vector ^a)] [f : (^a -> ^b)]) : (Vector ^b)
  (let ([len (vector-length-raw xs)])
    (vector/map-loop xs f len 0 (vector))))

(define (vector-filter [xs : (Vector ^a)] [pred : (^a -> Bool)]) : (Vector ^a)
  (let ([len (vector-length-raw xs)])
    (vector/filter-loop xs pred #t len 0 (vector))))

(define (vector-filter-not [xs : (Vector ^a)] [pred : (^a -> Bool)]) : (Vector ^a)
  (let ([len (vector-length-raw xs)])
    (vector/filter-loop xs pred #f len 0 (vector))))

(define (vector-foldl [xs : (Vector ^a)] [init : ^b] [f : (^b ^a -> ^b)]) : ^b
  (let ([len (vector-length-raw xs)])
    (vector/fold-loop xs f len 0 init)))

(define (vector-count [xs : (Vector ^a)] [pred : (^a -> Bool)]) : Int
  (vector/count-loop xs pred (vector-length-raw xs) 0 0))

;; Slicing

;; (vector-copy v start end) — slice [start, end).
(define (vector-copy [xs : (Vector ^a)] [start : Int] [end : Int]) : (Vector ^a)
  (vector/copy-loop xs end start (vector)))

;; (vector-take v n) — first n elements.
(define (vector-take [xs : (Vector ^a)] [n : Int]) : (Vector ^a)
  (vector/copy-loop xs n 0 (vector)))

;; (vector-drop v n) — skip the first n elements.
(define (vector-drop [xs : (Vector ^a)] [n : Int]) : (Vector ^a)
  (vector/copy-loop xs (vector-length-raw xs) n (vector)))

;; Sort

;; (vector-sort v less?) — returns a sorted copy.
(define (vector-sort [xs : (Vector ^a)] [less? : (^a ^a -> Bool)]) : (Vector ^a)
  (let ([tmp (vector->mutable-vector xs)])
    (vector/sort-loop! tmp less? (vector-length-raw xs) 1)
    (vector-create tmp)))

;; Search

;; (vector-member v x) — first index of x, or None.
(define (vector-member [xs : (Vector ^a)] [x : ^a]) : (Option Int)
  (let ([idx (vector-index-of-raw xs x)])
    (if (= idx -1)
      None
      (Some idx))))

;; (vector-argmin v f) — element minimizing (f x). Vector must be non-empty.
(define (vector-argmin [xs : (Vector ^a)] [f : (^a -> Int)]) : ^a
  (vector/arg-loop xs f (lambda ([a : Int] [b : Int]) (< a b))
                   (vector-length-raw xs) 1 0 (f (vector-item-raw xs 0))))

;; (vector-argmax v f) — element maximizing (f x). Vector must be non-empty.
(define (vector-argmax [xs : (Vector ^a)] [f : (^a -> Int)]) : ^a
  (vector/arg-loop xs f (lambda ([a : Int] [b : Int]) (> a b))
                   (vector-length-raw xs) 1 0 (f (vector-item-raw xs 0))))

;; Conversions

;; Mutable-Vector -> Vector via ImmutableArray.Create<T>(T[]).
(define (vector->immutable-vector [xs : (Mutable-Vector ^a)]) : (Vector ^a)
  (vector-create xs))

(export vector make-vector build-vector
        vector-length vector-ref vector-empty?
        vector-set/copy vector-append
        vector-map vector-filter vector-filter-not vector-foldl
        vector-copy vector-take vector-drop
        vector-sort vector-member vector-count
        vector-argmin vector-argmax
        vector->immutable-vector)
