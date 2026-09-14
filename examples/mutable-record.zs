;; Mutable record properties
;;
;; Mark a record (or struct) field `#:mutable` to allow mutation after
;; construction. Mutate it with the receiver form of set!:
;;
;;     (set! record-value field-name expr)
;;
;; The receiver must be a variable — a let-bound name, a parameter, or a
;; class field. `with` copy-updates and field accessors work exactly as on
;; immutable records; a mutable field is just one that can also be set.

(namespace ZScheme.Examples)
(module mutable-record)

(import-clr
  [holder-set-x ZScheme.Examples.PointHolder.SetX :instance : (PointHolder Int -> Int)])

(define-record Point [x : Int #:mutable] [y : Int])
(define-struct Triple [a : Int] [b : Int #:mutable] [c : Int])

;; A record stored in a class field, mutated through the field name
(define-class PointHolder
  [p : Point #:mutable]
  (define (SetX [n : Int]) : Int
    (begin (set! p x n) (Point/x p))))

;; Mutating a record bound in a let
(define (nudge) : Int
  (let ([p (Point 1 2)])
    (begin
      (set! p x (+ (Point/x p) 10))
      (Point/x p))))

;; A class record passed as a parameter is a reference, so the mutation is
;; visible to the caller
(define (bump [p : Point] [n : Int]) : Int
  (begin (set! p x (+ (Point/x p) n)) (Point/x p)))

;; A struct passed as a parameter is a copy, so it is not
(define (bump-triple [t : Triple] [n : Int]) : Int
  (begin (set! t b (+ (Triple/b t) n)) (Triple/b t)))

(define (main) : Int
  (let ([p (Point 1 2)])
    (let ([moved (bump p 10)])
      (let ([t (Triple 1 2 3)])
        (let ([ht (bump-triple t 100)])
          (let ([h (new PointHolder (Point 0 9))])
            (let ([set (holder-set-x h 33)])
              ;; nudge: 11
              ;; moved: 11 — and p was shared (class records are references)
              ;; with still copies: (Point/y (with p [y 5])) is 5, p.y stays 2
              ;; the struct copy: ht is 102, the caller's t is still 2
              ;; the class field receiver: 33
              (+ (nudge) moved (Point/x p) (Point/y (with p [y 5])) ht (Triple/b t) set))))))))
