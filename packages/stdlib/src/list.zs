;; list.zs — Singly linked list (mutable-vector used only for variadic constructor)
(module list)

(import stdlib/treelist)
(import stdlib/vector)
(import stdlib/mutable/vector)
(import stdlib/mutable/treelist)

(define-union (List ^a)
  (Nil)
  (Cons [head : ^a] [tail : (List ^a)]))

;; Internal loop helpers
;;
;; Only the loops with more than one caller live here. A loop that serves exactly one public
;; function is defined inside it instead, which lets it capture that function's arguments rather
;; than thread them through every recursive call. Both forms compile to the same thing: a nested
;; define is lifted to a top-level static with its captures as leading parameters, and a tail call
;; to it still becomes a loop.

(define (list/reverse-loop [xs : (List ^a)] [acc : (List ^a)]) : (List ^a)
  (match xs
    [Nil acc]
    [(Cons h t) (list/reverse-loop t (Cons h acc))]))

(define (list/from-mutable-vector-loop [elements : (Mutable-Vector ^a)] [i : Int] [acc : (List ^a)]) : (List ^a)
  (if (< i 0)
    acc
    (list/from-mutable-vector-loop elements (- i 1) (Cons (vector-ref elements i) acc))))

;; Public functions

(define (list [elements : ^a ...]) : (List ^a)
  (list/from-mutable-vector-loop elements (- (vector-length elements) 1) Nil))

(define (list/empty) : (List ^a) Nil)

(define (cons [x : ^a] [xs : (List ^a)]) : (List ^a)
  (Cons x xs))

(define (list-head [xs : (List ^a)]) : ^a
  (match xs
    [(Cons h _) h]
    [Nil (raise (new System.Exception "Called list-head on empty List"))]))

(define (list-tail [xs : (List ^a)]) : (List ^a)
  (match xs
    [(Cons _ t) t]
    [Nil (raise (new System.Exception "Called list-tail on empty List"))]))

(define (car [xs : (List ^a)]) : ^a
  (match xs
    [(Cons h _) h]
    [Nil (raise (new System.Exception "Called car on empty List"))]))

(define (cdr [xs : (List ^a)]) : (List ^a)
  (match xs
    [(Cons _ t) t]
    [Nil (raise (new System.Exception "Called cdr on empty List"))]))

(define (rest [xs : (List ^a)]) : (List ^a)
  (match xs
    [(Cons _ t) t]
    [Nil Nil]))

(define (empty? [xs : (List ^a)]) : Bool
  (match xs
    [Nil #t]
    [(Cons _ _) #f]))

(define (length [xs : (List ^a)]) : Int
  (define (loop [ys : (List ^a)] [acc : Int]) : Int
    (match ys
      [Nil acc]
      [(Cons _ t) (loop t (+ acc 1))]))
  (loop xs 0))

(define (list-ref [xs : (List ^a)] [n : Int]) : ^a
  (define (loop [ys : (List ^a)] [i : Int]) : ^a
    (match ys
      [Nil (raise (new System.Exception "Index out of bounds"))]
      [(Cons h t)
        (if (= i n)
          h
          (loop t (+ i 1)))]))
  (loop xs 0))

(define (reverse [xs : (List ^a)]) : (List ^a)
  (list/reverse-loop xs Nil))

(define (map [xs : (List ^a)] [f : (^a -> ^b)]) : (List ^b)
  (define (loop [ys : (List ^a)] [acc : (List ^b)]) : (List ^b)
    (match ys
      [Nil acc]
      [(Cons h t) (loop t (Cons (f h) acc))]))
  (list/reverse-loop (loop xs Nil) Nil))

(define (filter [xs : (List ^a)] [pred : (^a -> Bool)]) : (List ^a)
  (define (loop [ys : (List ^a)] [acc : (List ^a)]) : (List ^a)
    (match ys
      [Nil acc]
      [(Cons h t)
        (if (pred h)
          (loop t (Cons h acc))
          (loop t acc))]))
  (list/reverse-loop (loop xs Nil) Nil))

(define (fold [xs : (List ^a)] [init : ^b] [f : (^b ^a -> ^b)]) : ^b
  (define (loop [ys : (List ^a)] [acc : ^b]) : ^b
    (match ys
      [Nil acc]
      [(Cons h t) (loop t (f acc h))]))
  (loop xs init))

;; Consing the reversed prefix onto `ys` is exactly what list/reverse-loop does with a non-Nil
;; accumulator, so no separate loop is needed here.
(define (concat [xs : (List ^a)] [ys : (List ^a)]) : (List ^a)
  (list/reverse-loop (list/reverse-loop xs Nil) ys))

(define (append [xs : (List ^a)] [x : ^a]) : (List ^a)
  (concat xs (Cons x Nil)))

;; Conversion functions

(define (treelist->list [xs : (TreeList ^a)]) : (List ^a)
  (define (loop [i : Int] [acc : (List ^a)]) : (List ^a)
    (if (< i 0)
      acc
      (loop (- i 1) (Cons (treelist-ref xs i) acc))))
  (loop (- (treelist-length xs) 1) Nil))

(define (vector->list [xs : (Vector ^a)]) : (List ^a)
  (define (loop [i : Int] [acc : (List ^a)]) : (List ^a)
    (if (< i 0)
      acc
      (loop (- i 1) (Cons (vector-ref xs i) acc))))
  (loop (- (vector-length xs) 1) Nil))

(define (mutable-vector->list [xs : (Mutable-Vector ^a)]) : (List ^a)
  (list/from-mutable-vector-loop xs (- (vector-length xs) 1) Nil))

(define (mutable-treelist->list [xs : (Mutable-TreeList ^a)]) : (List ^a)
  (define (loop [i : Int] [acc : (List ^a)]) : (List ^a)
    (if (< i 0)
      acc
      (loop (- i 1) (Cons (mutable-treelist-ref xs i) acc))))
  (loop (- (mutable-treelist-length xs) 1) Nil))

(define (list->treelist [xs : (List ^a)]) : (TreeList ^a)
  (fold xs (treelist) (lambda ([acc : (TreeList ^a)] x) (treelist-add acc x))))

(define (list->vector [xs : (List ^a)]) : (Vector ^a)
  (fold xs (vector) (lambda ([acc : (Vector ^a)] x) (vector-append acc (vector x)))))

(define (list->mutable-treelist [xs : (List ^a)]) : (Mutable-TreeList ^a)
  (treelist-copy (list->treelist xs)))

(define (list->mutable-vector [xs : (List ^a)]) : (Mutable-Vector ^a)
  (vector->mutable-vector (list->vector xs)))

(export List Nil Cons list
        cons car cdr
        list/empty list-head list-tail rest empty?
        length list-ref reverse
        map filter fold
        append concat
        treelist->list vector->list mutable-vector->list mutable-treelist->list
        list->treelist list->vector list->mutable-treelist list->mutable-vector)
