(define (factorial [n : Int] [acc : Int]) : Int
  (if (= n 0) acc (factorial (- n 1) (* n acc))))
