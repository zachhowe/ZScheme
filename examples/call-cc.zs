(module call-cc-demo)

;; call-cc.zs — Demonstrates first-class continuations via (call/cc f).
;;
;; The (call/cc f) form captures the current continuation and applies the
;; user-supplied function f to it. f receives a callable continuation k that
;; — when invoked with a value v — aborts the current computation and resumes
;; the surrounding context as if (call/cc f) had returned v directly.
;;
;; Implementation: see "Continuations from Generalized Stack Inspection"
;; (Pettyjohn et al., ICFP 2005). Each non-tail call in capturable functions
;; is wrapped in a try/catch that, on a SaveContinuation throw, appends a
;; frame describing the post-call computation and rethrows.

;; --- Basic capture-and-resume ---
;; The continuation immediately invokes itself with 41, so r=41 and result=42.
(define (basic-callcc) : Int
  (let ([r (call/cc (lambda (k) (k 41)))])
    (+ r 1)))

;; --- Early exit ---
;; Pattern: (call/cc (lambda (escape) body)). Inside body, calling (escape v)
;; aborts the call/cc form and returns v from it.
(define (early-out [x : Int]) : Int
  (let ([r (call/cc (lambda (k)
            (if (= x 0)
              (k 99)
              x)))])
    (+ r 1)))

;; --- Capturing free variables ---
;; The continuation closes over `x` from the enclosing scope; that captured
;; value rides along in the saved frame.
(define (with-capture [x : Int]) : Int
  (let ([t (call/cc (lambda (k) (k 41)))])
    (+ t x)))

;; --- Two consecutive call/ccs ---
;; Tests nested transformation: each call/cc gets its own frame class and
;; sibling continuation function.
(define (two-callccs) : Int
  (let ([a (call/cc (lambda (ka) (ka 10)))])
    (let ([b (call/cc (lambda (kb) (kb 20)))])
      (+ a b))))

;; --- Multi-value continuation invocation ---
;; (k v1 v2 ... vn) auto-bundles into (k (values v1 ... vn)). α resolves to
;; (Int * Int * Int); the surrounding (let _ ...) gives ContinuationTransform
;; a frame to wrap, so the tuple flows through the frame's Invoke at resume.
(define (mv-callcc) : Int
  (let ([t (call/cc (lambda (k) (k 1 2 3)))])
    (let-values ([(a b c) t])
      (+ a (+ b c)))))

;; --- call-with-values + multi-value continuation ---
;; Producer thunk returns whatever k carried; consumer destructures it.
(define (mv-cwv) : Int
  (let ([t (call/cc (lambda (k) (k 10 20)))])
    (call-with-values (lambda () t) (lambda (a b) (+ a b)))))
