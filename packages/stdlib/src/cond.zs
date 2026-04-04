;; cond.zs — Cond macro (multi-branch conditional)
(module cond)

(export cond)

(define-syntax cond
  (syntax-rules (else)
    [(cond [else body ...])
     (begin body ...)]
    [(cond [test body ...] rest ...)
     (if test (begin body ...) (cond rest ...))]))
