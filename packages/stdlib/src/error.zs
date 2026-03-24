;; error.zs — Structured error type
(module error)

(import stdlib/option)

(record ErrorInfo [message : String] [cause : (Option ErrorInfo)])

(define (Error [msg : String]) : ErrorInfo
  (ErrorInfo msg None))

(export ErrorInfo Error)
