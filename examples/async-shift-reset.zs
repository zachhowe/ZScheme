(module async-shift-reset-demo)

;; async-shift-reset.zs — Demonstrates delimited continuations (shift/reset,
;; control/prompt, call/comp) inside [async] functions.
;;
;; The same machinery that lifts the async-vs-call/cc limitation lifts it for
;; the delimited operators too — each (shift k …) / (control k …) etc. throws
;; a tagged SaveContinuation, the in-flight catch wrappers extend it with a
;; frame, and the matching (reset …) / (prompt …) consumes it.

(define-async (fetch) : (Task Int) 5)

;; --- shift inside reset, with the reset entirely inside the async body ---
;; (shift k …) captures (+ r 10) as the delimited continuation. (k v) replays
;; that with v=5, yielding 15.
(define-async (shift-inside-async) : (Task Int)
  (let ([v (await (fetch))])
    (reset (let ([r (shift k (k v))]) (+ r 10)))))

;; --- control + prompt (Felleisen-style, no fresh prompt on resume) ---
(define-async (control-inside-async) : (Task Int)
  (let ([v (await (fetch))])
    (prompt (let ([r (control k (k v))]) (* r 2)))))

;; --- call/comp (Racket call-with-composable-continuation) ---
(define-async (callcomp-inside-async) : (Task Int)
  (prompt (let ([t (call/comp (lambda (k) (k 100)))]) (+ t 1))))

;; --- Tagged shift/reset around an async expression ---
;; The tag identifies which prompt to deliver to; ResumeAsync awaits async
;; frames the same way regardless of tag.
(define-async (tagged-shift-reset) : (Task Int)
  (let ([tag (make-prompt-tag)])
    (reset tag (let ([r (shift tag k (k 7))]) (+ r 3)))))
