;; error.zs — Structured error type
(module error)

(import stdlib/option)

;; Structured error with an optional inner cause, forming a chain
(define-record Error [message : String] [inner : (Option Error)])

;; Construct a leaf Error with no inner cause
(define (make-error [msg : String]) : Error
  (Error msg None))

;; Construct an Error wrapping `inner-err` as its cause
(define (make-error-with-inner [msg : String] [inner-err : Error]) : Error
  (Error msg (Some inner-err)))

(export make-error make-error-with-inner Error)
