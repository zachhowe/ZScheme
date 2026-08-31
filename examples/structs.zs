;; (define-struct ...) — declare a .NET value type.
;;
;; Mirrors (define-record ...) but emits a real CLR struct: a sealed type whose
;; BaseType is System.ValueType. C# codegen produces `readonly record struct`;
;; IL codegen produces an indistinguishable struct. Constructor calls,
;; field accessors, and (with ...) copy-updates work the same as records.

(namespace ZScheme.Examples)

(module structs)

;; A simple value-type point.
(define-struct Point [x : Int] [y : Int])

;; A struct with three fields.
(define-struct Rect [width : Int] [height : Int] [origin : Point])

;; Generic struct — type parameters work the same as on records.
;; (Note: avoid names like `Pair` that collide with built-in CLR aliases.)
(define-struct (Tuple2 a b) [fst : a] [snd : b])

;; Direct constructor: (Point 3 4)
(define (origin) : Point (Point 0 0))

;; The (new ...) form also works on user-defined structs (and records).
(define (make-point [x : Int] [y : Int]) : Point
  (new Point x y))

;; Field access: the accessor is `TypeName-field`.
(define (manhattan [p : Point]) : Int
  (+ (Point-x p) (Point-y p)))

;; (with ...) returns a fresh struct value; the original is unchanged
;; because structs have value semantics.
(define (shift-x [p : Point] [nx : Int]) : Point
  (with p [x nx]))

(define (move-to [p : Point] [nx : Int] [ny : Int]) : Point
  (with p [x nx] [y ny]))

;; Generic struct constructor.
(define (pair-of-ints [a : Int] [b : Int]) : (Tuple2 Int Int)
  (Tuple2 a b))

;; Returns 0 when value semantics hold: shifting `p` produces a new value;
;; the original p.x is still 3. (3 - 3) + (10 - 10) = 0.
(define (main [_args : (Mutable-Vector String)]) : Int
  (let* ([p (make-point 3 4)]
         [moved (shift-x p 10)])
    (+ (- (Point-x p) 3)
       (- (Point-x moved) 10))))
