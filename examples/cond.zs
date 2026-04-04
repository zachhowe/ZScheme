;; Multi-branch conditionals with cond

(namespace ZScheme.Examples)

(module cond-example)

(import stdlib/cond)

;; Classify a number's sign
(define (sign [x : Int]) : String
  (cond
    [(> x 0) "positive"]
    [(< x 0) "negative"]
    [else    "zero"]))

;; FizzBuzz using cond
(define (fizzbuzz [n : Int]) : String
  (cond
    [(= (% n 15) 0) "FizzBuzz"]
    [(= (% n 3) 0)  "Fizz"]
    [(= (% n 5) 0)  "Buzz"]
    [else            (int->string n)]))

;; Letter grade from a score
(define (grade [score : Int]) : String
  (cond
    [(>= score 90) "A"]
    [(>= score 80) "B"]
    [(>= score 70) "C"]
    [(>= score 60) "D"]
    [else          "F"]))
