;; List — a singly linked list built as a recursive union type
;; Demonstrates recursive functions over an algebraic data structure

(namespace ZScheme.Examples)

(module list-example)

(import stdlib/list)

(import-clr
  [println System.Console/WriteLine])

;; Sum all elements by recursing through the list
(define (sum [xs : (List Int)]) : Int
  (match xs
    [Nil 0]
    [(Cons h t) (+ h (sum t))]))

;; Count how many elements satisfy a predicate
(define (count-where [xs : (List Int)] [pred : (Int -> Bool)]) : Int
  (match xs
    [Nil 0]
    [(Cons h t)
      (if (pred h)
        (+ 1 (count-where t pred))
        (count-where t pred))]))

;; Build a range [lo, lo+1, ..., hi-1] as a List
(define (range-loop [lo : Int] [hi : Int] [acc : (List Int)]) : (List Int)
  (if (= lo hi)
    acc
    (range-loop lo (- hi 1) (Cons (- hi 1) acc))))

(define (range [lo : Int] [hi : Int]) : (List Int)
  (range-loop lo hi Nil))

;; Check if any element satisfies a predicate
(define (any? [xs : (List Int)] [pred : (Int -> Bool)]) : Bool
  (match xs
    [Nil #f]
    [(Cons h t)
      (if (pred h) #t (any? t pred))]))

;; Zip two lists into a list of pairs (truncates to shorter length)
(define (zip [xs : (List Int)] [ys : (List String)]) : (List String)
  (match xs
    [Nil Nil]
    [(Cons x xt)
      (match ys
        [Nil Nil]
        [(Cons y yt)
          (Cons (string-append (string-append (int->string x) ": ") y)
                 (zip xt yt))])]))

;; Putting it all together
(define (main) : Int
  (let ([nums (range 1 6)])                                  ;; (1 2 3 4 5)
    (let ([doubled (map nums (lambda (x) (* x 2)))])       ;; (2 4 6 8 10)
      (let ([evens (filter nums (lambda (x) (= (% x 2) 0)))]) ;; (2 4)
        (begin
          (println (string-append "nums:    " (int->string (sum nums))))        ;; 15
          (println (string-append "doubled: " (int->string (sum doubled))))     ;; 30
          (println (string-append "evens:   " (int->string (length evens)))) ;; 2
          (println (string-append "any >3?  " (if (any? nums (lambda (x) (> x 3))) "yes" "no")))
          (println (string-append "count >3: " (int->string (count-where nums (lambda (x) (> x 3))))))
          (println (string-append "list sum: " (int->string (sum (list 10 20 30)))))
          0)))))
