;; slist.zs — Singly linked list (mutable-array used only for variadic constructor)
(module slist)

(import stdlib/list)
(import stdlib/array)
(import stdlib/mutable/array)
(import stdlib/mutable/list)

(define-union (SList ^a)
  (SNil)
  (SCons [head : ^a] [tail : (SList ^a)]))

;; Internal loop helpers

(define (slist/reverse-loop [xs : (SList ^a)] [acc : (SList ^a)]) : (SList ^a)
  (match xs
    [SNil acc]
    [(SCons h t) (slist/reverse-loop t (SCons h acc))]))

(define (slist/map-loop [xs : (SList ^a)] [f : (^a -> ^b)] [acc : (SList ^b)]) : (SList ^b)
  (match xs
    [SNil acc]
    [(SCons h t) (slist/map-loop t f (SCons (f h) acc))]))

(define (slist/filter-loop [xs : (SList ^a)] [pred : (^a -> Bool)] [acc : (SList ^a)]) : (SList ^a)
  (match xs
    [SNil acc]
    [(SCons h t)
      (if (pred h)
        (slist/filter-loop t pred (SCons h acc))
        (slist/filter-loop t pred acc))]))

(define (slist/fold-loop [xs : (SList ^a)] [f : (^b ^a -> ^b)] [acc : ^b]) : ^b
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

(define (slist/from-array-loop [elements : (Mutable-Array ^a)] [i : Int] [acc : (SList ^a)]) : (SList ^a)
  (if (< i 0)
    acc
    (slist/from-array-loop elements (- i 1) (SCons (array-ref elements i) acc))))

(define (slist/from-list-loop [elements : (List ^a)] [i : Int] [acc : (SList ^a)]) : (SList ^a)
  (if (< i 0)
    acc
    (slist/from-list-loop elements (- i 1) (SCons (list-ref elements i) acc))))

(define (slist/from-immutable-array-loop [elements : (Array ^a)] [i : Int] [acc : (SList ^a)]) : (SList ^a)
  (if (< i 0)
    acc
    (slist/from-immutable-array-loop elements (- i 1) (SCons (array-ref elements i) acc))))

(define (slist/from-mutable-list-loop [elements : (Mutable-List ^a)] [i : Int] [acc : (SList ^a)]) : (SList ^a)
  (if (< i 0)
    acc
    (slist/from-mutable-list-loop elements (- i 1) (SCons (list-ref elements i) acc))))

;; Public functions

(define (slist [elements : ^a ...]) : (SList ^a)
  (slist/from-array-loop elements (- (array-length elements) 1) SNil))


(define (slist/empty) : (SList ^a)
  SNil)

(define (cons [x : ^a] [xs : (SList ^a)]) : (SList ^a)
  (SCons x xs))

(define (list-head [xs : (SList ^a)]) : ^a
  (match xs
    [(SCons h _) h]
    [SNil (raise (new System.Exception "Called list-head on empty SList"))]))

(define (list-tail [xs : (SList ^a)]) : (SList ^a)
  (match xs
    [(SCons _ t) t]
    [SNil (raise (new System.Exception "Called list-tail on empty SList"))]))

(define (car [xs : (SList ^a)]) : ^a
  (match xs
    [(SCons h _) h]
    [SNil (raise (new System.Exception "Called car on empty SList"))]))

(define (cdr [xs : (SList ^a)]) : (SList ^a)
  (match xs
    [(SCons _ t) t]
    [SNil (raise (new System.Exception "Called cdr on empty SList"))]))

(define (rest [xs : (SList ^a)]) : (SList ^a)
  (match xs
    [(SCons _ t) t]
    [SNil SNil]))

(define (empty? [xs : (SList ^a)]) : Bool
  (match xs
    [SNil #t]
    [(SCons _ _) #f]))

(define (length [xs : (SList ^a)]) : Int
  (slist/length-loop xs 0))

(define (list-ref [xs : (SList ^a)] [n : Int]) : ^a
  (slist/nth-loop xs 0 n))

(define (reverse [xs : (SList ^a)]) : (SList ^a)
  (slist/reverse-loop xs SNil))

(define (map [xs : (SList ^a)] [f : (^a -> ^b)]) : (SList ^b)
  (slist/reverse-loop (slist/map-loop xs f SNil) SNil))

(define (filter [xs : (SList ^a)] [pred : (^a -> Bool)]) : (SList ^a)
  (slist/reverse-loop (slist/filter-loop xs pred SNil) SNil))

(define (fold [xs : (SList ^a)] [init : ^b] [f : (^b ^a -> ^b)]) : ^b
  (slist/fold-loop xs f init))

(define (concat [xs : (SList ^a)] [ys : (SList ^a)]) : (SList ^a)
  (slist/concat-loop (slist/reverse-loop xs SNil) ys))

(define (append [xs : (SList ^a)] [x : ^a]) : (SList ^a)
  (concat xs (SCons x SNil)))

;; Conversion functions

(define (list->slist [xs : (List ^a)]) : (SList ^a)
  (slist/from-list-loop xs (- (length xs) 1) SNil))

(define (array->slist [xs : (Array ^a)]) : (SList ^a)
  (slist/from-immutable-array-loop xs (- (array-length xs) 1) SNil))

(define (mutable-array->slist [xs : (Mutable-Array ^a)]) : (SList ^a)
  (slist/from-array-loop xs (- (array-length xs) 1) SNil))

(define (mutable-list->slist [xs : (Mutable-List ^a)]) : (SList ^a)
  (slist/from-mutable-list-loop xs (- (length xs) 1) SNil))

(define (slist->list [xs : (SList ^a)]) : (List ^a)
  (fold xs (list) (lambda ([acc : (List ^a)] x) (append acc x))))

(define (slist->array [xs : (SList ^a)]) : (Array ^a)
  (fold xs (array) (lambda ([acc : (Array ^a)] x) (append acc x))))

(define (slist->mutable-list [xs : (SList ^a)]) : (Mutable-List ^a)
  (list->mutable-list (slist->list xs)))

(define (slist->mutable-array [xs : (SList ^a)]) : (Mutable-Array ^a)
  (array->mutable-array (slist->array xs)))

(export SList SNil SCons slist
        cons car cdr
        slist/empty list-head list-tail rest empty?
        length list-ref reverse
        map filter fold
        append concat
        list->slist array->slist mutable-array->slist mutable-list->slist
        slist->list slist->array slist->mutable-list slist->mutable-array)
