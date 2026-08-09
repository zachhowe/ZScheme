;; middleware.zs — request pipeline (Use)
(module middleware)

;; UseExtensions.Use lives in the Microsoft.AspNetCore.Builder namespace but ships
;; in Microsoft.AspNetCore.Http.Abstractions.dll. The middleware's `next` parameter
;; is a 0-arg thunk `(-> Task)` (Func<Task>), which selects the
;; Use(IApplicationBuilder, Func<HttpContext, Func<Task>, Task>) overload over the
;; RequestDelegate-shaped one (whose next is arity-1).
(import-clr
  Microsoft.AspNetCore.Builder
  Microsoft.AspNetCore.Http

  [clr-use UseExtensions/Use
    :from "Microsoft.AspNetCore.Http.Abstractions"
    : (IApplicationBuilder (HttpContext (-> Task) -> Task) -> IApplicationBuilder)])

;; Register a middleware: (fn ctx next) where `next` is a thunk that returns a
;; Task. Call `(next)` to invoke the rest of the pipeline. WebApplication
;; implements IApplicationBuilder; upcast and discard the returned builder.
(define (app/use [app : WebApplication]
                 [middleware : (HttpContext (-> Task) -> Task)]) : Unit
  (let ([ab : IApplicationBuilder app])
    (clr-use ab middleware) ()))

(export app/use)
