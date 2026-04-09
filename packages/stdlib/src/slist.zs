;; slist.zs — Singly linked list (pure ZScheme, no CLR interop)
(module slist)

(union (SList ^a)
  (SNil)
  (SCons [head : ^a] [tail : (SList ^a)]))

;; Internal loop helpers

(define (slist/reverse-loop [xs : (SList ^a)] [acc : (SList ^a)]) : (SList ^a)
  (match xs
    [SNil acc]
    [(SCons h t) (slist/reverse-loop t (SCons h acc))]))

(define (slist/map-loop [xs : (SList ^a)] [f : (Fn [^a] ^b)] [acc : (SList ^b)]) : (SList ^b)
  (match xs
    [SNil acc]
    [(SCons h t) (slist/map-loop t f (SCons (f h) acc))]))

(define (slist/filter-loop [xs : (SList ^a)] [pred : (Fn [^a] Bool)] [acc : (SList ^a)]) : (SList ^a)
  (match xs
    [SNil acc]
    [(SCons h t)
      (if (pred h)
        (slist/filter-loop t pred (SCons h acc))
        (slist/filter-loop t pred acc))]))

(define (slist/fold-loop [xs : (SList ^a)] [f : (Fn [^b ^a] ^b)] [acc : ^b]) : ^b
  (match xs
    [SNil acc]
    [(SCons h t) (slist/fold-loop t f (f acc h))]))

(define (slist/length-loop [xs : (SList ^a)] [acc : Int]) : Int
  (match xs
    [SNil acc]
    [(SCons _ t) (slist/length-loop t (+ acc 1))]))

(define (slist/nth-loop [xs : (SList ^a)] [i : Int] [target : Int]) : ^a
  (match xs
    [SNil (raise (new System.Exception "Index out of bounds"))]
    [(SCons h t)
      (if (= i target)
        h
        (slist/nth-loop t (+ i 1) target))]))

(define (slist/concat-loop [reversed-xs : (SList ^a)] [ys : (SList ^a)]) : (SList ^a)
  (match reversed-xs
    [SNil ys]
    [(SCons h t) (slist/concat-loop t (SCons h ys))]))

;; Public functions

(define (slist/empty) : (SList ^a)
  SNil)

(define (slist/cons [x : ^a] [xs : (SList ^a)]) : (SList ^a)
  (SCons x xs))

(define (slist/head [xs : (SList ^a)]) : ^a
  (match xs
    [(SCons h _) h]
    [SNil (raise (new System.Exception "Called head on empty SList"))]))

(define (slist/tail [xs : (SList ^a)]) : (SList ^a)
  (match xs
    [(SCons _ t) t]
    [SNil (raise (new System.Exception "Called tail on empty SList"))]))

(define (slist/empty? [xs : (SList ^a)]) : Bool
  (match xs
    [SNil #t]
    [(SCons _ _) #f]))

(define (slist/length [xs : (SList ^a)]) : Int
  (slist/length-loop xs 0))

(define (slist/nth [xs : (SList ^a)] [n : Int]) : ^a
  (slist/nth-loop xs 0 n))

(define (slist/reverse [xs : (SList ^a)]) : (SList ^a)
  (slist/reverse-loop xs SNil))

(define (slist/map [xs : (SList ^a)] [f : (Fn [^a] ^b)]) : (SList ^b)
  (slist/reverse-loop (slist/map-loop xs f SNil) SNil))

(define (slist/filter [xs : (SList ^a)] [pred : (Fn [^a] Bool)]) : (SList ^a)
  (slist/reverse-loop (slist/filter-loop xs pred SNil) SNil))

(define (slist/fold [xs : (SList ^a)] [init : ^b] [f : (Fn [^b ^a] ^b)]) : ^b
  (slist/fold-loop xs f init))

(define (slist/concat [xs : (SList ^a)] [ys : (SList ^a)]) : (SList ^a)
  (slist/concat-loop (slist/reverse-loop xs SNil) ys))

(define (slist/append [xs : (SList ^a)] [x : ^a]) : (SList ^a)
  (slist/concat xs (SCons x SNil)))

(export SList SNil SCons
        slist/empty slist/cons slist/head slist/tail slist/empty?
        slist/length slist/nth slist/reverse
        slist/map slist/filter slist/fold
        slist/append slist/concat)
