(module shift-reset-demo)

;; shift-reset.zs — Demonstrates delimited continuations via (reset e) and
;; (shift k e).
;;
;; (reset e) installs a prompt boundary; the form has the same type as e.
;; (shift k e) captures the continuation up to the dynamically innermost
;; (reset). The captured k is composable: invoking (k v) replays the captured
;; slice with v threaded through and *returns the result* — unlike call/cc,
;; which aborts. This means the shift body can call k zero, one, or many
;; times and combine the results normally.
;;
;; Reference: Danvy & Filinski, "Abstracting Control" (LFP 1990).
;;
;; Frame synthesis is the same machinery used for (call/cc): every non-tail
;; call inside a capturable function gets wrapped so the resumption can replay
;; the post-call computation. The compiler's CapturableCallHoister pre-pass
;; A-normalizes value-consuming sub-expression positions, so a (shift k …)
;; appearing as a sub-expression of a BinOp / If condition / Match scrutinee /
;; call argument / etc. has its surrounding context captured automatically —
;; no manual let-binding is required.

;; --- Basic discard: k unused ---
;; (* 2 (reset (let v (shift k 10) (+ 1 v)))) — k is discarded, the captured
;; "+ 1 _" frame is thrown away, reset returns 10.
(define (discard-k) : Int
  (let ([r (reset
            (let ([v (shift k 10)])
              (+ 1 v)))])
    (* 2 r)))

;; --- Composable resumption ---
;; k captures "(+ 1 _)". (k 10) yields 11; reset returns 11; * 2 = 22.
(define (compose-k) : Int
  (let ([r (reset
            (let ([v (shift k (k 10))])
              (+ 1 v)))])
    (* 2 r)))

;; --- Multi-shot resumption ---
;; The shift body invokes k twice. With no captured frames, (k 1) = 1 and
;; (k 2) = 2; (+ (k 1) (k 2)) = 3.
(define (multi-shot) : Int
  (reset (shift k (+ (k 1) (k 2)))))

;; --- Free variables ride along in the captured frame ---
(define (with-capture [x : Int]) : Int
  (reset
    (let ([v (shift k (k 1))])
      (+ x v))))

;; --- Nested resets target the innermost ---
;; The inner reset returns 99 directly. Then the outer shift k2 captures
;; "(+ a _)" with a=99 fixed, (k2 10) = 109.
(define (nested) : Int
  (reset
    (let ([a (reset (shift k1 99))])
      (let ([v (shift k2 (k2 10))])
        (+ a v)))))

;; --- Multi-value shift ---
;; (k v1 v2) auto-bundles to (k (values v1 v2)); the captured continuation's
;; α resolves to (Int * Int). The post-shift context is kept inside a (let _ ...)
;; so ContinuationTransform synthesizes a frame around the destructuring.
(define (mv-shift) : Int
  (reset
    (let ([t (shift k (k 7 8))])
      (let-values ([(a b) t]) (+ a b)))))

;; --- Capture through a sub-expression position ---
;; (shift k (k 7)) used directly in BinOp.Right — the surrounding (* 2 (+ 100 _))
;; context is captured automatically by the CapturableCallHoister pre-pass.
;; (k 7) → +100 = 107 → *2 = 214.
(define (sub-expr-capture) : Int
  (reset (* 2 (+ 100 (shift k (k 7))))))
