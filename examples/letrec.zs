(namespace ZScheme.Examples)

(module letrec)

;; Recursive local bindings with letrec
;; Every name in the group is in scope in every binding's value, which is what
;; `let` and `let*` cannot do: their value is evaluated in the enclosing scope.

;; Mutual recursion — the reason the form exists. Neither function can be
;; written with let/let*, because each needs the other already in scope.
(define (even-count [n : Int]) : Int
  (letrec ([even? (lambda ([k : Int]) : Bool (if (= k 0) #t (odd? (- k 1))))]
           [odd? (lambda ([k : Int]) : Bool (if (= k 0) #f (even? (- k 1))))])
    (if (even? n) 1 0)))

;; Self recursion — a local helper that does not need to be a top-level define.
(define (sum-to [n : Int]) : Int
  (letrec ([sum (lambda ([k : Int]) : Int (if (= k 0) 0 (+ k (sum (- k 1)))))])
    (sum n)))

;; Tail-recursive accumulation. The self-call is in tail position, so both
;; backends emit the lifted function as a loop and this runs in constant stack.
(define (count-down [n : Int]) : Int
  (letrec ([loop (lambda ([k : Int] [acc : Int]) : Int
                   (if (= k 0) acc (loop (- k 1) (+ acc 1))))])
    (loop n 0)))

;; Closing over the enclosing scope: `factor` is a local of the outer function
;; and is captured by the recursive helper.
(define (scale-sum [factor : Int] [n : Int]) : Int
  (letrec ([go (lambda ([k : Int]) : Int (if (= k 0) 0 (+ factor (go (- k 1)))))])
    (go n)))

;; A mixed group: plain values and functions together. Initialization still runs
;; left to right, so a non-lambda binding may only use bindings already in place
;; — `base` here. A function's body is free to name anything in the group,
;; because it only runs once the whole group is initialized.
(define (quadratic [x : Int]) : Int
  (letrec ([base (* x x)]
           [step (lambda ([k : Int]) : Int (+ k base))]
           [result (step x)])
    result))

;; Passing a letrec binding as a value rather than calling it directly.
(define (apply-twice [g : (Int -> Int)] [n : Int]) : Int
  (g (g n)))

(define (twice-decrement [n : Int]) : Int
  (letrec ([dec (lambda ([k : Int]) : Int (if (<= k 0) 0 (- k 1)))])
    (apply-twice dec n)))
