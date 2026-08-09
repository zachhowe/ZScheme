;; cancellation.zs — CancellationTokenSource / CancellationToken via CLR interop
(module cancellation)

;; CLR bindings (internal). CancellationTokenSource/CancellationToken are non-generic
;; runtime types, so they are referenced by their full CLR names (cf. datetime.zs)
;; rather than via `define-type-alias`, which only round-trips for generic/array types.
(import-clr
  System.Threading

  [cts-token CancellationTokenSource.Token
    :instance-property : (CancellationTokenSource -> CancellationToken)]
  [cts-cancel CancellationTokenSource.Cancel
    :instance : (CancellationTokenSource -> Unit)]
  [cts-cancel-after CancellationTokenSource.CancelAfter
    :instance : (CancellationTokenSource Int -> Unit)]
  [cts-is-requested CancellationTokenSource.IsCancellationRequested
    :instance-property : (CancellationTokenSource -> Bool)]
  [cts-dispose CancellationTokenSource.Dispose
    :instance : (CancellationTokenSource -> Unit)]
  [token-is-requested CancellationToken.IsCancellationRequested
    :instance-property : (CancellationToken -> Bool)]
  ;; CancellationToken.None is a static get-only property returning the struct
  ;; (same shape as System.DateTime/Now in datetime.zs).
  [token-none CancellationToken/None
    :instance-property : (-> CancellationToken)])

;; Exported functions

;; Create a new cancellation source.
(define (cancellation/new) : CancellationTokenSource
  (new CancellationTokenSource))

;; Create a source that cancels itself after `millis` milliseconds.
(define (cancellation/new-with-timeout [millis : Int]) : CancellationTokenSource
  (new CancellationTokenSource millis))

;; The token associated with a source (a struct view over it).
(define (cancellation/token [src : CancellationTokenSource])
  : CancellationToken
  (cts-token src))

;; Request cancellation.
(define (cancellation/cancel! [src : CancellationTokenSource]) : Unit
  (cts-cancel src))

;; Schedule cancellation after `millis` milliseconds.
(define (cancellation/cancel-after! [src : CancellationTokenSource] [millis : Int])
  : Unit
  (cts-cancel-after src millis))

;; Has cancellation been requested on the source?
(define (cancellation/requested? [src : CancellationTokenSource]) : Bool
  (cts-is-requested src))

;; Has cancellation been requested on the token?
(define (cancellation/token-requested? [token : CancellationToken]) : Bool
  (token-is-requested token))

;; Release the resources held by the source.
(define (cancellation/dispose! [src : CancellationTokenSource]) : Unit
  (cts-dispose src))

;; A token that can never be canceled.
(define (cancellation/none) : CancellationToken
  (token-none))

(export cancellation/new cancellation/new-with-timeout
        cancellation/token cancellation/cancel! cancellation/cancel-after!
        cancellation/requested? cancellation/token-requested?
        cancellation/dispose! cancellation/none)
