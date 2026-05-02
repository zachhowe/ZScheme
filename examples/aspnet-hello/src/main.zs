;; aspnet-hello — minimal ASP.NET Core app demonstrating the aspnet wrapper.
(module main)

(import aspnet/app)
(import aspnet/router)
(import aspnet/request)
(import aspnet/response)
(import aspnet/middleware)

;; Logging middleware — adds an X-Logged response header and continues.
(define-async (log-middleware
                [ctx : Microsoft.AspNetCore.Http.HttpContext]
                [next : (-> Task)])
  : Task
  (begin
    (response/header-set ctx "X-Logged" (request/method ctx))
    (await (next))))

(define-async (handle-hello [ctx : Microsoft.AspNetCore.Http.HttpContext]) : Task
  (await (response/write-string ctx "hello world")))

(define-async (handle-user [ctx : Microsoft.AspNetCore.Http.HttpContext]) : Task
  (let [id (request/route-value ctx "id" "?")]
    (await (response/write-string ctx (string-append "user " id)))))

(define-async (handle-search [ctx : Microsoft.AspNetCore.Http.HttpContext]) : Task
  (let [q (request/query ctx "q" "")]
    (await (response/write-string ctx (string-append "search: " q)))))

(define-async (handle-echo [ctx : Microsoft.AspNetCore.Http.HttpContext]) : Task
  (let [body (await (request/read-body-string ctx))]
    (await (response/write-json ctx body))))

(define (main) : Unit
  (let [builder (app/create-builder)]
    (let [application (app/build builder)]
      (begin
        (app/use application log-middleware)
        (route/get application "/hello" handle-hello)
        (route/get application "/users/{id}" handle-user)
        (route/get application "/search" handle-search)
        (route/post application "/echo" handle-echo)
        (app/run application)))))
