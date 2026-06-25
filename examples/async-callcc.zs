(module async-callcc-demo)

;; async-callcc.zs — Demonstrates first-class continuations via (call/cc f)
;; inside [async] functions.
;;
;; ZScheme used to reject continuation capture inside async functions whose body
;; contains an (await ...). The post-call code in those functions lives inside
;; an IAsyncStateMachine.MoveNext, so a SaveContinuation thrown from there would
;; be caught by AsyncTaskMethodBuilder and SetException'd on the returned Task,
;; skipping frame extension and corrupting the captured continuation list.
;;
;; ContinuationTransform now splits the body at each non-tail capturable call,
;; synthesizes a continuation function that holds the post-call code, and —
;; when that post-call code crosses an await — marks the continuation function
;; async and emits an InvokeAsync method on the frame class.
;; Runtime.RunAsync drives ResumeAsync, which awaits async frames without
;; blocking the dispatch loop.

;; --- call/cc with no post-call code (tail position) ---
;; The captured continuation has zero frames; the throw escapes to RunAsync.
(define-async (callcc-tail) : (Task Int)
  (call/cc (lambda (k) (k 99))))

;; --- call/cc followed by sync code ---
;; The synthesized __cont function stays sync because (+ v 1) has no await.
(define-async (callcc-sync-tail) : (Task Int)
  (let ([v (call/cc (lambda (k) (k 41)))])
    (+ v 1)))

;; --- call/cc followed by await ---
;; The synthesized __cont function is async because its body awaits (fetch v);
;; the parent body wraps the tail call to __cont in (await ...) and the frame
;; class implements InvokeAsync.
(define-async (fetch [n : Int]) : (Task Int) (+ n 1))

(define-async (callcc-async-tail) : (Task Int)
  (let ([v (call/cc (lambda (k) (k 41)))])
    (await (fetch v))))

;; --- await before, call/cc after ---
;; The await prefix runs first; call/cc captures the (now-finished) post-await
;; continuation. The continuation has no remaining post-call code in this
;; function, so it returns directly to the caller.
(define-async (await-then-callcc) : (Task Int)
  (let ([a (await (fetch 9))])
    (call/cc (lambda (k) (k a)))))

;; --- await before AND after, with call/cc in the middle ---
;; Two non-tail positions, so two synthesized cont functions; the inner one is
;; async (post-call awaits); the outer captures (await (fetch v)) as its
;; post-call code, which is also async.
(define-async (await-callcc-await) : (Task Int)
  (let ([a (await (fetch 1))])
    (let ([v (call/cc (lambda (k) (k a)))])
      (await (fetch v)))))
