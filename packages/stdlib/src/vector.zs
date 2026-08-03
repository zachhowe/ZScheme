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
  [vector-length-raw ImmutableArray.Length
    :instance-property : ((Vector ^a) -> Int)]
  [vector-item-raw ImmutableArray.Item
    :instance-indexer : ((Vector ^a) Int -> ^a)]
  [vector-add-raw ImmutableArray.Add
    :instance : ((Vector ^a) ^a -> (Vector ^a))]
  [vector-add-range-raw ImmutableArray.AddRange
    :instance : ((Vector ^a) (Vector ^a) -> (Vector ^a))]
  [vector-set-raw ImmutableArray.SetItem
    :instance : ((Vector ^a) Int ^a -> (Vector ^a))]
  [vector-index-of-raw ImmutableArray.IndexOf
    :instance : ((Vector ^a) ^a -> Int)]
  [vector-create ImmutableArray/Create ^a
    : ((Mutable-Vector ^a) -> (Vector ^a))])

;; Constructors

(define (vector [elements : ^a ...]) : (Vector ^a)
  (vector-create elements))

;; Internal loop helpers
;;
;; Only the loops with more than one caller live here. A loop that serves exactly one public
;; function is defined inside it instead, which lets it capture that function's arguments — and the
;; hoisted length — rather than thread them through every recursive call. Both forms compile to the
;; same thing: a nested define is lifted to a top-level static with its captures as leading
;; parameters, and a tail call to it still becomes a loop.

;; Serves both vector-filter and vector-filter-not, via the keep? flag.
(define (vector/filter-loop [xs : (Vector ^a)] [pred : (^a -> Bool)] [keep? : Bool] [len : Int] [i : Int] [acc : (Vector ^a)]) : (Vector ^a)
  (if (= i len)
    acc
    (let ([item (vector-item-raw xs i)])
      (if (= (pred item) keep?)
        (vector/filter-loop xs pred keep? len (+ i 1) (vector-add-raw acc item))
        (vector/filter-loop xs pred keep? len (+ i 1) acc)))))

;; Serves vector-copy, vector-take and vector-drop.
(define (vector/copy-loop [xs : (Vector ^a)] [end : Int] [i : Int] [acc : (Vector ^a)]) : (Vector ^a)
  (if (= i end)
    acc
    (vector/copy-loop xs end (+ i 1) (vector-add-raw acc (vector-item-raw xs i)))))

;; Serves both vector-argmin and vector-argmax, via the less? comparator.
(define (vector/arg-loop [xs : (Vector ^a)] [f : (^a -> Int)] [less? : (Int Int -> Bool)] [len : Int] [i : Int] [best-idx : Int] [best-key : Int]) : ^a
  (if (= i len)
    (vector-item-raw xs best-idx)
    (let ([k (f (vector-item-raw xs i))])
      (if (less? k best-key)
        (vector/arg-loop xs f less? len (+ i 1) i k)
        (vector/arg-loop xs f less? len (+ i 1) best-idx best-key)))))

;; (build-vector n proc) — element i = (proc i) for i in [0, n).
(define (build-vector [n : Int] [f : (Int -> ^a)]) : (Vector ^a)
  (define (loop [i : Int] [acc : (Vector ^a)]) : (Vector ^a)
    (if (= i n)
      acc
      (loop (+ i 1) (vector-add-raw acc (f i)))))
  (loop 0 (vector)))

;; (make-vector n v) — length-n vector filled with v.
(define (make-vector [n : Int] [v : ^a]) : (Vector ^a)
  (define (loop [i : Int] [acc : (Vector ^a)]) : (Vector ^a)
    (if (= i n)
      acc
      (loop (+ i 1) (vector-add-raw acc v))))
  (loop 0 (vector)))

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
  (let ([n (vector-length vs)])
    (define (loop [i : Int] [acc : (Vector ^a)]) : (Vector ^a)
      (if (= i n)
        acc
        (loop (+ i 1) (vector-add-range-raw acc (vector-ref vs i)))))
    (loop 0 (vector))))

;; Iteration / transformation

(define (vector-map [xs : (Vector ^a)] [f : (^a -> ^b)]) : (Vector ^b)
  (let ([len (vector-length-raw xs)])
    (define (loop [i : Int] [acc : (Vector ^b)]) : (Vector ^b)
      (if (= i len)
        acc
        (loop (+ i 1) (vector-add-raw acc (f (vector-item-raw xs i))))))
    (loop 0 (vector))))

(define (vector-filter [xs : (Vector ^a)] [pred : (^a -> Bool)]) : (Vector ^a)
  (let ([len (vector-length-raw xs)])
    (vector/filter-loop xs pred #t len 0 (vector))))

(define (vector-filter-not [xs : (Vector ^a)] [pred : (^a -> Bool)]) : (Vector ^a)
  (let ([len (vector-length-raw xs)])
    (vector/filter-loop xs pred #f len 0 (vector))))

(define (vector-foldl [xs : (Vector ^a)] [init : ^b] [f : (^b ^a -> ^b)]) : ^b
  (let ([len (vector-length-raw xs)])
    (define (loop [i : Int] [acc : ^b]) : ^b
      (if (= i len)
        acc
        (loop (+ i 1) (f acc (vector-item-raw xs i)))))
    (loop 0 init)))

(define (vector-count [xs : (Vector ^a)] [pred : (^a -> Bool)]) : Int
  (let ([len (vector-length-raw xs)])
    (define (loop [i : Int] [acc : Int]) : Int
      (if (= i len)
        acc
        (loop (+ i 1)
          (if (pred (vector-item-raw xs i)) (+ acc 1) acc))))
    (loop 0 0)))

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

;; (vector-sort v less?) — returns a sorted copy. In-place insertion sort over a Mutable-Vector
;; buffer: shift! walks an element down into place, sort-loop! drives it across the buffer. Both
;; capture tmp and less?, so neither has to be threaded through the other's recursive calls.
(define (vector-sort [xs : (Vector ^a)] [less? : (^a ^a -> Bool)]) : (Vector ^a)
  (let ([tmp (vector->mutable-vector xs)])
    (define n (vector-length-raw xs))
    (define (shift! [j : Int] [v : ^a]) : Unit
      (if (= j 0)
        (vector-set! tmp 0 v)
        (let ([prev (vector-ref tmp (- j 1))])
          (if (less? v prev)
            (begin
              (vector-set! tmp j prev)
              (shift! (- j 1) v))
            (vector-set! tmp j v)))))
    (define (sort-loop! [i : Int]) : Unit
      (if (>= i n)
        ()
        (begin
          (shift! i (vector-ref tmp i))
          (sort-loop! (+ i 1)))))
    (sort-loop! 1)
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
