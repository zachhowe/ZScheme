;; aspnet-hello — minimal ASP.NET Core app demonstrating the aspnet wrapper.
(module main)

(import aspnet/app)
(import aspnet/router)
(import aspnet/request)
(import aspnet/response)
(import aspnet/middleware)
(import aspnet/services)
(import aspnet/logging)
(import di-abstractions/services)
(import logging-abstractions/core)

;; A service registered in the DI container and resolved per request by handle-greet.
(define-record Greeter [prefix : String])

;; Logging middleware — writes a structured log line via ILogger (default providers
;; are left intact by app/create-builder) and also tags the response for visibility.
(define-async (log-middleware
                [ctx : Microsoft.AspNetCore.Http.HttpContext]
                [next : (-> Task)])
  : Task
  (let ([logger (logging/request-logger ctx "AspNetHello")])
    (log/info logger "{Method} {Path}" (request/method ctx) (request/path ctx))
    (response/header-set ctx "X-Logged" (request/method ctx))
    (await (next))))

(define-async (handle-hello [ctx : Microsoft.AspNetCore.Http.HttpContext]) : Task
  (await (response/write-string ctx "hello world")))

(define-async (handle-user [ctx : Microsoft.AspNetCore.Http.HttpContext]) : Task
  (let ([id (request/route-value ctx "id" "?")])
    (await (response/write-string ctx (string-append "user " id)))))

(define-async (handle-search [ctx : Microsoft.AspNetCore.Http.HttpContext]) : Task
  (let ([q (request/query ctx "q" "")])
    (await (response/write-string ctx (string-append "search: " q)))))

(define-async (handle-echo [ctx : Microsoft.AspNetCore.Http.HttpContext]) : Task
  (let ([body (await (request/read-body-string ctx))])
    (await (response/write-json ctx body))))

;; Resolve the Greeter service from the request's scoped provider and use it. The
;; `: Greeter` annotation pins the generic GetRequiredService<T> instantiation to T = Greeter.
(define-async (handle-greet [ctx : Microsoft.AspNetCore.Http.HttpContext]) : Task
  (let ([g : Greeter (services/get-required-service (request/services ctx))])
    (await (response/write-string ctx (string-append (Greeter/prefix g) " world")))))

(define (main) : Unit
  (let ([builder (app/create-builder)])
    ;; Register a Greeter singleton on builder.Services before building the app.
    (services/add-singleton-instance (services/builder-services builder)
                                     (typeof Greeter) (Greeter "hello"))
    (let ([application (app/build builder)])
      (app/use application log-middleware)
      (route/get application "/hello" handle-hello)
      (route/get application "/users/{id}" handle-user)
      (route/get application "/search" handle-search)
      (route/get application "/greet" handle-greet)
      (route/post application "/echo" handle-echo)
      (app/run application))))
