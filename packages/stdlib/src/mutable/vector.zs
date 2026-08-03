;; mutable-vector.zs — Mutable-Vector operations via T[]
(module mutable-vector)

;; Map the ZScheme name `Mutable-Vector` to a CLR single-dimension array (T[]) at codegen.
(define-type-alias (Mutable-Vector ^a) :array)

;; Vector is referenced below but its canonical declaration lives in stdlib/vector, which
;; imports this module (Vector<->Mutable-Vector cycle), so it can't be imported here.
;; Re-declare it locally — must mirror the canonical target exactly.
(define-type-alias (Vector ^a)
  System.Collections.Immutable.ImmutableArray :from "System.Collections.Immutable")

;; CLR bindings (internal)
(import-clr
  System
  System.Linq
  [mv-length-raw Array.Length
    :instance-property : ((Mutable-Vector ^a) -> Int)]
  [mv-item-raw Array.Item
    :instance-indexer : ((Mutable-Vector ^a) Int -> ^a)]
  [mv-set-item-raw Array.Item
    :instance-indexer-set : ((Mutable-Vector ^a) Int ^a -> Unit)]
  [vector-to-mutable-raw Enumerable/ToArray ^a
    : ((Vector ^a) -> (Mutable-Vector ^a))])

;; Every loop in this module serves exactly one public function, so each is defined inside its
;; caller and captures that function's arguments instead of threading them through every recursive
;; call. A nested define is lifted to a top-level static with its captures as leading parameters,
;; and a tail call to it still becomes a loop, so this costs nothing at runtime.

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
  (let ([len (mv-length-raw xs)])
    (define (loop! [i : Int]) : Unit
      (if (= i len)
        ()
        (begin
          (mv-set-item-raw xs i (f (mv-item-raw xs i)))
          (loop! (+ i 1)))))
    (loop! 0)))

;; (vector-fill! v x) — set every slot to x.
(define (vector-fill! [xs : (Mutable-Vector ^a)] [x : ^a]) : Unit
  (let ([n (mv-length-raw xs)])
    (define (loop! [i : Int]) : Unit
      (if (= i n)
        ()
        (begin
          (mv-set-item-raw xs i x)
          (loop! (+ i 1)))))
    (loop! 0)))

;; (vector-copy! dst dst-start src src-start length) — copy src[src-start..+length] into dst[dst-start..].
(define (vector-copy! [dst : (Mutable-Vector ^a)] [dst-start : Int]
                       [src : (Mutable-Vector ^a)] [src-start : Int]
                       [length : Int]) : Unit
  (define (loop! [i : Int]) : Unit
    (if (= i length)
      ()
      (begin
        (mv-set-item-raw dst (+ dst-start i) (mv-item-raw src (+ src-start i)))
        (loop! (+ i 1)))))
  (loop! 0))

;; (vector-sort! v less?) — sort in place by the supplied predicate. In-place insertion sort:
;; O(n^2) but simple, and it avoids needing a CLR Comparison<T> delegate. shift! walks an element
;; down into place, sort-loop! drives it across the array; both capture xs and less?.
(define (vector-sort! [xs : (Mutable-Vector ^a)] [less? : (^a ^a -> Bool)]) : Unit
  (let ([n (mv-length-raw xs)])
    (define (shift! [j : Int] [v : ^a]) : Unit
      (if (= j 0)
        (mv-set-item-raw xs 0 v)
        (let ([prev (mv-item-raw xs (- j 1))])
          (if (less? v prev)
            (begin
              (mv-set-item-raw xs j prev)
              (shift! (- j 1) v))
            (mv-set-item-raw xs j v)))))
    (define (sort-loop! [i : Int]) : Unit
      (if (>= i n)
        ()
        (begin
          (shift! i (mv-item-raw xs i))
          (sort-loop! (+ i 1)))))
    (sort-loop! 1)))

;; Conversions

;; Vector -> Mutable-Vector via Enumerable.ToArray<T>.
(define (vector->mutable-vector [xs : (Vector ^a)]) : (Mutable-Vector ^a)
  (vector-to-mutable-raw xs))

(export vector-length vector-ref vector-empty?
        vector-set! vector-map! vector-fill! vector-copy! vector-sort!
        vector->mutable-vector)
