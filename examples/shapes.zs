;; Union types, records, and pattern matching

(namespace ZScript.Examples)

(module shapes)

(record Point [x : Int] [y : Int])

(union Shape
  (Circle [radius : Int])
  (Rect [w : Int] [h : Int]))

;; Compute area using pattern matching on constructors
(define (area [s : Shape]) : Int
  (match s
    [(Circle r) (* r r)]
    [(Rect w h) (* w h)]))

;; Describe a value using literal and wildcard patterns
(define (describe [n : Int]) : String
  (match n
    [0 "zero"]
    [1 "one"]
    [_ "other"]))

;; Create a point offset from origin
(define (offset [dx : Int] [dy : Int]) : Point
  (Point dx dy))
