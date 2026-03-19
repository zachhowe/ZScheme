(namespace ZScript.Examples)

(module factorial)

(define (factorial [n : Int] [acc : Int]) : Int
  (if (= n 0) acc (factorial (- n 1) (* n acc))))
