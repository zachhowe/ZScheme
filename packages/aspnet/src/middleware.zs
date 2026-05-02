;; middleware.zs — request pipeline (Use)
(module middleware)

(import-clr
  Microsoft.AspNetCore.Builder
  Microsoft.AspNetCore.Http
  ZScheme.AspNet.Bridge

  ;; Register a middleware: (fn ctx next) where `next` is a thunk that
  ;; returns a Task. Call `(next-call)` to invoke the rest of the pipeline.
  [app/use ZScheme.AspNet.Bridge.MiddlewareBridge/Use
    : (Microsoft.AspNetCore.Builder.WebApplication
       (Microsoft.AspNetCore.Http.HttpContext (-> Task) -> Task)
       -> Unit)])

(export app/use)
