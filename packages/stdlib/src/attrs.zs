;; attrs.zs — Attribute helper macros
(module attrs)

(export with-method-impl)

(define-syntax with-method-impl
  (syntax-rules (aggressive-inlining no-inlining no-optimization)
    [(with-method-impl aggressive-inlining body ...)
     (begin
       (@ System.Runtime.CompilerServices.MethodImplAttribute
          System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)
       body ...)]
    [(with-method-impl no-inlining body ...)
     (begin
       (@ System.Runtime.CompilerServices.MethodImplAttribute
          System.Runtime.CompilerServices.MethodImplOptions.NoInlining)
       body ...)]
    [(with-method-impl no-optimization body ...)
     (begin
       (@ System.Runtime.CompilerServices.MethodImplAttribute
          System.Runtime.CompilerServices.MethodImplOptions.NoOptimization)
       body ...)]))
