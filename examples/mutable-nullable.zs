;; Mutable properties and nullable value types
;; Demonstrates #:mutable fields on classes and Type? nullable syntax

(namespace ZScheme.Examples)

(module mutable-nullable)

;; Interface requiring mutable properties and nullable types
(interface ITimer
  (GetName [] : String)
  (GetDuration [] : Float?)
  (IsActive [] : Bool))

;; Class with mutable fields and nullable types
(class Timer : ITimer
  [name : String #:mutable]
  [duration : Float? #:mutable]
  [active : Bool #:mutable]

  (define (GetName) : String name)
  (define (GetDuration) : Float? duration)
  (define (IsActive) : Bool active))

;; Class with immutable and mutable fields mixed
(class Counter
  [label : String]
  [count : Int #:mutable]

  (define (GetLabel) : String label)
  (define (GetCount) : Int count))
