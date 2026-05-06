;; Record copy-updates via the `with` special form.
;;
;; Compiles to C#'s `with` expression in both backends:
;;   (with point [x 10])  ->  point with { X = 10 }
;; The IL backend emits `<Clone>$` on records and uses the clone + init-set
;; IL pattern, so decompilers render the call site as `x with { ... }`.

(namespace ZScheme.Examples)

(module with-form)

(define-record Point [x : Int] [y : Int])

(define-record Person
  [name : String]
  [age : Int]
  [email : String])

;; Single-field update — returns a new Point with the given x.
(define (shift-x [p : Point] [new-x : Int]) : Point
  (with p [x new-x]))

;; Multi-field update — both coordinates replaced at once.
(define (move-to [p : Point] [nx : Int] [ny : Int]) : Point
  (with p [x nx] [y ny]))

;; Chained with expressions — inner first, outer second.
(define (birthday-and-rename [p : Person] [new-name : String]) : Person
  (with (with p [age (+ (Person/age p) 1)])
        [name new-name]))

;; Returns a fresh Point at the origin, leaving the input untouched.
(define (to-origin [p : Point]) : Point
  (with p [x 0] [y 0]))
