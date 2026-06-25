;; control.zs — when/unless conditional macros (execute body for effect)
;;
;; These mirror Racket's `when`/`unless`. Because ZScheme's `if` requires both
;; branches and unifies their types, the body is unified against the `()` (Unit)
;; branch — so the body must be Unit-typed (side-effecting). `(when test 5)`
;; will not type-check; use these forms for effects, like Racket's void result.
(module control)

(export when unless)

;; (when test body ...) — evaluate body when test is true; () otherwise
(define-syntax when
  (syntax-rules ()
    [(when test body ...)
     (if test (begin body ...) ())]))

;; (unless test body ...) — evaluate body when test is false; () otherwise
(define-syntax unless
  (syntax-rules ()
    [(unless test body ...)
     (if test () (begin body ...))]))
