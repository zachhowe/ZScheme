;; mutable-vector.zs — Mutable-Vector operations via T[]
(module mutable-vector)

;; Map the ZScheme name `Mutable-Vector` to a CLR single-dimension array (T[]) at codegen.
(define-type-alias (Mutable-Vector ^a) :array)

;; CLR bindings (internal)
(import-clr
  System
  System.Linq
  [mv-length-raw System.Array.Length
    :instance-property : ((Mutable-Vector ^a) -> Int)]
  [mv-item-raw System.Array.Item
    :instance-indexer : ((Mutable-Vector ^a) Int -> ^a)]
  [mv-set-item-raw System.Array.Item
    :instance-indexer-set : ((Mutable-Vector ^a) Int ^a -> Unit)]
  [vector-to-mutable-raw System.Linq.Enumerable/ToArray ^a
    : ((Vector ^a) -> (Mutable-Vector ^a))])

;; Internal loop helpers

(define (mv/map-in-place-loop! [xs : (Mutable-Vector ^a)] [f : (^a -> ^a)] [len : Int] [i : Int]) : Unit
  (if (= i len)
    ()
    (begin
      (mv-set-item-raw xs i (f (mv-item-raw xs i)))
      (mv/map-in-place-loop! xs f len (+ i 1)))))

(define (mv/copy-loop! [dst : (Mutable-Vector ^a)] [dst-start : Int]
                       [src : (Mutable-Vector ^a)] [src-start : Int]
                       [length : Int] [i : Int]) : Unit
  (if (= i length)
    ()
    (begin
      (mv-set-item-raw dst (+ dst-start i) (mv-item-raw src (+ src-start i)))
      (mv/copy-loop! dst dst-start src src-start length (+ i 1)))))

(define (mv/fill-loop! [xs : (Mutable-Vector ^a)] [x : ^a] [n : Int] [i : Int]) : Unit
  (if (= i n)
    ()
    (begin
      (mv-set-item-raw xs i x)
      (mv/fill-loop! xs x n (+ i 1)))))

;; In-place insertion sort. O(n^2) but simple and avoids needing a CLR
;; Comparison<T> delegate.
(define (mv/sort-shift! [arr : (Mutable-Vector ^a)] [less? : (^a ^a -> Bool)] [j : Int] [v : ^a]) : Unit
  (if (= j 0)
    (mv-set-item-raw arr 0 v)
    (let [prev (mv-item-raw arr (- j 1))]
      (if (less? v prev)
        (begin
          (mv-set-item-raw arr j prev)
          (mv/sort-shift! arr less? (- j 1) v))
        (mv-set-item-raw arr j v)))))

(define (mv/sort-loop! [arr : (Mutable-Vector ^a)] [less? : (^a ^a -> Bool)] [n : Int] [i : Int]) : Unit
  (if (>= i n)
    ()
    (begin
      (mv/sort-shift! arr less? i (mv-item-raw arr i))
      (mv/sort-loop! arr less? n (+ i 1)))))

;; Length and access

(define (vector-length [xs : (Mutable-Vector ^a)]) : Int
  (mv-length-raw xs))

(define (vector-ref [xs : (Mutable-Vector ^a)] [i : Int]) : ^a
  (mv-item-raw xs i))

(define (vector-set! [xs : (Mutable-Vector ^a)] [i : Int] [val : ^a]) : Unit
  (mv-set-item-raw xs i val))

(define (vector-empty? [xs : (Mutable-Vector ^a)]) : Bool
  (= (mv-length-raw xs) 0))

;; In-place operations

;; (vector-map! v f) — apply f to each slot in place.
(define (vector-map! [xs : (Mutable-Vector ^a)] [f : (^a -> ^a)]) : Unit
  (mv/map-in-place-loop! xs f (mv-length-raw xs) 0))

;; (vector-fill! v x) — set every slot to x.
(define (vector-fill! [xs : (Mutable-Vector ^a)] [x : ^a]) : Unit
  (mv/fill-loop! xs x (mv-length-raw xs) 0))

;; (vector-copy! dst dst-start src src-start length) — copy src[src-start..+length] into dst[dst-start..].
(define (vector-copy! [dst : (Mutable-Vector ^a)] [dst-start : Int]
                       [src : (Mutable-Vector ^a)] [src-start : Int]
                       [length : Int]) : Unit
  (mv/copy-loop! dst dst-start src src-start length 0))

;; (vector-sort! v less?) — sort in place by the supplied predicate.
(define (vector-sort! [xs : (Mutable-Vector ^a)] [less? : (^a ^a -> Bool)]) : Unit
  (mv/sort-loop! xs less? (mv-length-raw xs) 1))

;; Conversions

;; Vector -> Mutable-Vector via Enumerable.ToArray<T>.
(define (vector->mutable-vector [xs : (Vector ^a)]) : (Mutable-Vector ^a)
  (vector-to-mutable-raw xs))

(export vector-length vector-ref vector-empty?
        vector-set! vector-map! vector-fill! vector-copy! vector-sort!
        vector->mutable-vector)
