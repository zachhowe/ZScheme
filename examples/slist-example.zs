;; SList — a singly linked list built as a recursive union type
;; Demonstrates recursive functions over an algebraic data structure

(namespace ZScheme.Examples)

(module slist-example)

(import stdlib/slist)

(import-clr
  [println System.Console/WriteLine])

;; Sum all elements by recursing through the list
(define (sum [xs : (SList Int)]) : Int
  (match xs
    [SNil 0]
    [(SCons h t) (+ h (sum t))]))

;; Count how many elements satisfy a predicate
(define (count-where [xs : (SList Int)] [pred : (Fn [Int] Bool)]) : Int
  (match xs
    [SNil 0]
    [(SCons h t)
      (if (pred h)
        (+ 1 (count-where t pred))
        (count-where t pred))]))

;; Build a range [lo, lo+1, ..., hi-1] as an SList
(define (range-loop [lo : Int] [hi : Int] [acc : (SList Int)]) : (SList Int)
  (if (= lo hi)
    acc
    (range-loop lo (- hi 1) (SCons (- hi 1) acc))))

(define (range [lo : Int] [hi : Int]) : (SList Int)
  (range-loop lo hi SNil))

;; Check if any element satisfies a predicate
(define (any? [xs : (SList Int)] [pred : (Fn [Int] Bool)]) : Bool
  (match xs
    [SNil #f]
    [(SCons h t)
      (if (pred h) #t (any? t pred))]))

;; Zip two lists into a list of pairs (truncates to shorter length)
(define (zip [xs : (SList Int)] [ys : (SList String)]) : (SList String)
  (match xs
    [SNil SNil]
    [(SCons x xt)
      (match ys
        [SNil SNil]
        [(SCons y yt)
          (SCons (string-append (string-append (int->string x) ": ") y)
                 (zip xt yt))])]))

;; Putting it all together
(define (main) : Int
  (let [nums (range 1 6)]                                  ;; (1 2 3 4 5)
    (let [doubled (slist/map nums (fn [x] (* x 2)))]       ;; (2 4 6 8 10)
      (let [evens (slist/filter nums (fn [x] (= (% x 2) 0)))] ;; (2 4)
        (begin
          (println (string-append "nums:    " (int->string (sum nums))))        ;; 15
          (println (string-append "doubled: " (int->string (sum doubled))))     ;; 30
          (println (string-append "evens:   " (int->string (slist/length evens)))) ;; 2
          (println (string-append "any >3?  " (if (any? nums (fn [x] (> x 3))) "yes" "no")))
          (println (string-append "count >3: " (int->string (count-where nums (fn [x] (> x 3))))))
          0)))))
