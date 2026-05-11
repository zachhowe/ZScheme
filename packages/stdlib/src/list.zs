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

(define (list/reverse-loop [xs : (List ^a)] [acc : (List ^a)]) : (List ^a)
  (match xs
    [Nil acc]
    [(Cons h t) (list/reverse-loop t (Cons h acc))]))

(define (list/map-loop [xs : (List ^a)] [f : (^a -> ^b)] [acc : (List ^b)]) : (List ^b)
  (match xs
    [Nil acc]
    [(Cons h t) (list/map-loop t f (Cons (f h) acc))]))

(define (list/filter-loop [xs : (List ^a)] [pred : (^a -> Bool)] [acc : (List ^a)]) : (List ^a)
  (match xs
    [Nil acc]
    [(Cons h t)
      (if (pred h)
        (list/filter-loop t pred (Cons h acc))
        (list/filter-loop t pred acc))]))

(define (list/fold-loop [xs : (List ^a)] [f : (^b ^a -> ^b)] [acc : ^b]) : ^b
  (match xs
    [Nil acc]
    [(Cons h t) (list/fold-loop t f (f acc h))]))

(define (list/length-loop [xs : (List ^a)] [acc : Int]) : Int
  (match xs
    [Nil acc]
    [(Cons _ t) (list/length-loop t (+ acc 1))]))

(define (list/nth-loop [xs : (List ^a)] [i : Int] [target : Int]) : ^a
  (match xs
    [Nil (raise (new System.Exception "Index out of bounds"))]
    [(Cons h t)
      (if (= i target)
        h
        (list/nth-loop t (+ i 1) target))]))

(define (list/concat-loop [reversed-xs : (List ^a)] [ys : (List ^a)]) : (List ^a)
  (match reversed-xs
    [Nil ys]
    [(Cons h t) (list/concat-loop t (Cons h ys))]))

(define (list/from-mutable-vector-loop [elements : (Mutable-Vector ^a)] [i : Int] [acc : (List ^a)]) : (List ^a)
  (if (< i 0)
    acc
    (list/from-mutable-vector-loop elements (- i 1) (Cons (vector-ref elements i) acc))))

(define (list/from-treelist-loop [elements : (TreeList ^a)] [i : Int] [acc : (List ^a)]) : (List ^a)
  (if (< i 0)
    acc
    (list/from-treelist-loop elements (- i 1) (Cons (treelist-ref elements i) acc))))

(define (list/from-vector-loop [elements : (Vector ^a)] [i : Int] [acc : (List ^a)]) : (List ^a)
  (if (< i 0)
    acc
    (list/from-vector-loop elements (- i 1) (Cons (vector-ref elements i) acc))))

(define (list/from-mutable-treelist-loop [elements : (Mutable-TreeList ^a)] [i : Int] [acc : (List ^a)]) : (List ^a)
  (if (< i 0)
    acc
    (list/from-mutable-treelist-loop elements (- i 1) (Cons (mutable-treelist-ref elements i) acc))))

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
  (list/length-loop xs 0))

(define (list-ref [xs : (List ^a)] [n : Int]) : ^a
  (list/nth-loop xs 0 n))

(define (reverse [xs : (List ^a)]) : (List ^a)
  (list/reverse-loop xs Nil))

(define (map [xs : (List ^a)] [f : (^a -> ^b)]) : (List ^b)
  (list/reverse-loop (list/map-loop xs f Nil) Nil))

(define (filter [xs : (List ^a)] [pred : (^a -> Bool)]) : (List ^a)
  (list/reverse-loop (list/filter-loop xs pred Nil) Nil))

(define (fold [xs : (List ^a)] [init : ^b] [f : (^b ^a -> ^b)]) : ^b
  (list/fold-loop xs f init))

(define (concat [xs : (List ^a)] [ys : (List ^a)]) : (List ^a)
  (list/concat-loop (list/reverse-loop xs Nil) ys))

(define (append [xs : (List ^a)] [x : ^a]) : (List ^a)
  (concat xs (Cons x Nil)))

;; Conversion functions

(define (treelist->list [xs : (TreeList ^a)]) : (List ^a)
  (list/from-treelist-loop xs (- (treelist-length xs) 1) Nil))

(define (vector->list [xs : (Vector ^a)]) : (List ^a)
  (list/from-vector-loop xs (- (vector-length xs) 1) Nil))

(define (mutable-vector->list [xs : (Mutable-Vector ^a)]) : (List ^a)
  (list/from-mutable-vector-loop xs (- (vector-length xs) 1) Nil))

(define (mutable-treelist->list [xs : (Mutable-TreeList ^a)]) : (List ^a)
  (list/from-mutable-treelist-loop xs (- (mutable-treelist-length xs) 1) Nil))

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
