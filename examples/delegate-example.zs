(namespace ZScheme.Examples)

(module delegate-example)

;; Demonstrates the (delegate ...) type form for specifying .NET delegate types.
;; This bypasses the compiler's default mapping of function types to System.Func<> /
;; System.Action<> and is needed when a CLR API expects a specific delegate type.

(import stdlib/string)

(import-clr
  [invoke-action System.Console/WriteLine])

;; Pass a lambda as a specific delegate type (System.Action)
(define (run-action [action : (delegate System.Action)]) : Unit
  (action))

;; Pass a lambda as a specific delegate type (System.Func<int,int>)
(define (run-func [f : (delegate System.Func<int,int>)]) : Int
  (f 10))

;; Accept a typed parameter, then call the delegate directly
(define (wrap-delegate [callback : (delegate System.Action)]) : Unit
  (callback))

;; Top-level usage
(define (main [args : (Mutable-Vector String)]) : Int
  (run-action (lambda ()
    (invoke-action "Hello from delegate!")))

  (let ([result (run-func (lambda ([x : Int]) : Int
    (* 2 x)))])
    (invoke-action (format "Result: {}" (int->string result))))

  0)
