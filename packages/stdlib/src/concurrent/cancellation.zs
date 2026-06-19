;; cancellation.zs — CancellationTokenSource / CancellationToken via CLR interop
(module cancellation)

;; CLR bindings (internal). CancellationTokenSource/CancellationToken are non-generic
;; runtime types, so they are referenced by their full CLR names (cf. datetime.zs)
;; rather than via `define-type-alias`, which only round-trips for generic/array types.
(import-clr
  System.Threading

  [cts-token System.Threading.CancellationTokenSource.Token
    :instance-property : (System.Threading.CancellationTokenSource
                          -> System.Threading.CancellationToken)]
  [cts-cancel System.Threading.CancellationTokenSource.Cancel
    :instance : (System.Threading.CancellationTokenSource -> Unit)]
  [cts-cancel-after System.Threading.CancellationTokenSource.CancelAfter
    :instance : (System.Threading.CancellationTokenSource Int -> Unit)]
  [cts-is-requested System.Threading.CancellationTokenSource.IsCancellationRequested
    :instance-property : (System.Threading.CancellationTokenSource -> Bool)]
  [cts-dispose System.Threading.CancellationTokenSource.Dispose
    :instance : (System.Threading.CancellationTokenSource -> Unit)]
  [token-is-requested System.Threading.CancellationToken.IsCancellationRequested
    :instance-property : (System.Threading.CancellationToken -> Bool)]
  ;; CancellationToken.None is a static get-only property returning the struct
  ;; (same shape as System.DateTime/Now in datetime.zs).
  [token-none System.Threading.CancellationToken/None
    :instance-property : (-> System.Threading.CancellationToken)])

;; Exported functions

;; Create a new cancellation source.
(define (cancellation/new) : System.Threading.CancellationTokenSource
  (new System.Threading.CancellationTokenSource))

;; Create a source that cancels itself after `millis` milliseconds.
(define (cancellation/new-with-timeout [millis : Int]) : System.Threading.CancellationTokenSource
  (new System.Threading.CancellationTokenSource millis))

;; The token associated with a source (a struct view over it).
(define (cancellation/token [src : System.Threading.CancellationTokenSource])
  : System.Threading.CancellationToken
  (cts-token src))

;; Request cancellation.
(define (cancellation/cancel! [src : System.Threading.CancellationTokenSource]) : Unit
  (cts-cancel src))

;; Schedule cancellation after `millis` milliseconds.
(define (cancellation/cancel-after! [src : System.Threading.CancellationTokenSource] [millis : Int])
  : Unit
  (cts-cancel-after src millis))

;; Has cancellation been requested on the source?
(define (cancellation/requested? [src : System.Threading.CancellationTokenSource]) : Bool
  (cts-is-requested src))

;; Has cancellation been requested on the token?
(define (cancellation/token-requested? [token : System.Threading.CancellationToken]) : Bool
  (token-is-requested token))

;; Release the resources held by the source.
(define (cancellation/dispose! [src : System.Threading.CancellationTokenSource]) : Unit
  (cts-dispose src))

;; A token that can never be cancelled.
(define (cancellation/none) : System.Threading.CancellationToken
  (token-none))

(export cancellation/new cancellation/new-with-timeout
        cancellation/token cancellation/cancel! cancellation/cancel-after!
        cancellation/requested? cancellation/token-requested?
        cancellation/dispose! cancellation/none)
