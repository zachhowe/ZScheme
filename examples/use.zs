(module use-demo)

;; use.zs — Demonstrates the (use ...) and (use* ...) special forms:
;; deterministic disposal of IDisposable resources, like C#'s `using`
;; statement / F#'s `use`. The bound resource is disposed when the body's
;; scope exits, whether normally or via an exception. The resource type must
;; implement System.IDisposable (checked at compile time).

(import-clr
  [ms-can-read System.IO.MemoryStream.CanRead
    :instance-property : (System.IO.MemoryStream -> Bool)])

;; Open a MemoryStream and use it; it is disposed automatically at scope exit.
;; We return the resource itself to observe that it was disposed afterwards —
;; a disposed MemoryStream reports CanRead = false.
(define (disposed-after-use) : Bool
  (let ([s (use ([m (new System.IO.MemoryStream)]) m)])
    (not (ms-can-read s))))

;; use* binds several resources at once and disposes them in reverse order
;; (innermost first) when the body's scope exits.
(define (two-resources-disposed) : Bool
  (let ([a (new System.IO.MemoryStream)])
    (let ([b (new System.IO.MemoryStream)])
      (begin
        (use* ([x a] [y b]) 0)
        (and (not (ms-can-read a)) (not (ms-can-read b)))))))
