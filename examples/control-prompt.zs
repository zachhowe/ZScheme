(module control-prompt-demo)

;; control-prompt.zs — Demonstrates Felleisen-style delimited continuations
;; (control / prompt), Racket's call-with-composable-continuation (call/comp),
;; tagged prompts (make-prompt-tag / prompt tag / shift tag / control tag /
;; call/comp f tag), and the divergence between shift and control on resume.
;;
;; Reference: Felleisen, "The Theory and Practice of First-Class Prompts"
;; (POPL 1988); Racket's `racket/control`.

;; --- (prompt e) is an alias for (reset e) ---
(define (prompt-alias) : Int
  (prompt (control k (k 10))))                     ;; => 10

;; --- (control k body): captured k composes Felleisen-style ---
;; With no captured frames: (k 7) returns 7; prompt returns 7.
(define (control-basic) : Int
  (prompt (control k (k 7))))                      ;; => 7

;; --- (call/comp f) ≡ (control k (f k)) ---
(define (callcomp-basic) : Int
  (prompt (call/comp (lambda (k) (k 42)))))            ;; => 42

;; --- Tagged prompts: outer shift skips the inner default-tagged prompt ---
;; The inner prompt has a different tag, so the tagged shift propagates past
;; it to the outer ResetAt.
(define (tagged-skips-inner) : Int
  (let ([t (make-prompt-tag)])
    (prompt t
      (prompt
        (shift t k 99)))))                         ;; => 99

;; --- Multi-shot resume of a composable continuation ---
;; The let around the control binds the captured-continuation frame: the
;; "post-control" computation is "+ 1 r". Each (k v) replays this with
;; v threaded in. (k 5) = 6, (k 6) = 7. Body returns 7.
;; Frame synthesis only fires around (let v <non-tail-call> body) shapes —
;; that's how call/cc and shift/reset work too — so we use a let here.
(define (multi-shot-control) : Int
  (prompt
    (let ([r (control k (k (k 5)))])
      (+ 1 r))))                                   ;; => 7

;; --- make-prompt-tag returns distinct tags ---
;; Each call allocates a fresh tag; nested prompts with different tags are
;; isolated.
(define (distinct-tags) : Int
  (let ([t1 (make-prompt-tag)])
    (let ([t2 (make-prompt-tag)])
      (prompt t1
        (prompt t2
          (shift t1 k 200))))))                    ;; => 200, captures past t2

;; --- Multi-value control / call/comp ---
;; Both Felleisen-style operators support multi-value invocation via the same
;; auto-bundling rewrite as call/cc. Result type is the tuple of arg types.
(define (mv-control) : Int
  (prompt
    (let ([t (control k (k 100 200))])
      (let-values ([(a b) t]) (+ a b)))))          ;; => 300

(define (mv-callcomp) : Int
  (prompt
    (let ([t (call/comp (lambda (k) (k 1 2 3)))])
      (let-values ([(a b c) t]) (+ a (+ b c))))))  ;; => 6
