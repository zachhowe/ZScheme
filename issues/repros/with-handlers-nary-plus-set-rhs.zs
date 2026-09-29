(module minrepro23)

(import stdlib/treelist)

(struct SRec_1 [f0 : Int] [f1 : Int] [f2 : Int #:mutable])

(define (compute) : Int
  (let ([x408 (SRec_1 0 0 0)])  (begin (set! x408 f2 (treelist-fold (treelist 0 50 0 0) 0 (lambda ([x492 : Int] [x493 : Int]) (+ 5 (with-handlers ([System.InvalidOperationException x499] (% x492 19)) 0) 0 0 0)))) (SRec_1-f2 x408))))
