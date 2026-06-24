;; aspnet-logging-tests.zs — structured-logging integration tests for the aspnet wrapper.
;;
;; Exercises the full ILogger path end-to-end: acquire a category logger from the
;; request-scoped provider, then call the variadic log/* verbs (including the
;; templated, multi-arg form) from both middleware and a route handler. Providers are
;; cleared (test-support/build-test-app), so nothing is written, but every binding —
;; ILoggerFactory.CreateLogger, the LoggerExtensions Log* params-array methods, and the
;; variadic→object[] packing — is still invoked over real HTTP without error.
(namespace ZScheme.AspNet.Tests)
(module aspnet-logging-tests)

(import zunit)
(import http)
(import aspnet/app)
(import aspnet/router)
(import aspnet/request)
(import aspnet/response)
(import aspnet/logging)
(import test-support)

(import-clr
  Microsoft.AspNetCore.Http
  Microsoft.AspNetCore.Builder)

;; Middleware that logs each request via a category logger before continuing.
(define-async (log-middleware [ctx : HttpContext] [next : (-> Task)]) : Task
  (let ([logger (logging/request-logger ctx "Test.Middleware")])
    (log/info logger "request {Method} {Path}"
              (request/method ctx) (request/path ctx))
    (await (next))))

;; Handler that logs at several levels (plain, templated, and with no args) then responds.
(define-async (handle-logged [ctx : HttpContext]) : Task
  (let ([logger (logging/request-logger ctx "Test.Handler")])
    (log/debug logger "entering handler")
    (log/info logger "handling user {Id}" 42)
    (log/warning logger "slow path {Name} took {Ms}ms" "lookup" 12)
    (log/error logger "no-arg error message")
    (await (response/write-string ctx "logged"))))

(test-suite-async AspNetLoggingTests
  ;; A handler resolves a logger and logs at multiple levels without error.
  (test-case-async logs_from_handler
    (let ([app (test-support/build-test-app)])
      (route/get app "/logged" handle-logged)
      (let ([app (await (test-support/start-test-app app))])
        (let ([first-url (app/first-url app)])
          (let ([result (await (http/get (string-append first-url "/logged") (treelist)))])
            (check-equal? 200 (HttpResponse/status (unwrap result)))
            (check-equal? "logged" (HttpResponse/body (unwrap result))))
          (test-support/shutdown-test-server app)))))

  ;; Middleware resolves a logger and logs a templated message, then the pipeline continues.
  (test-case-async logs_from_middleware
    (let ([app (test-support/build-test-app)])
      (app/use app log-middleware)
      (route/get app "/hello" test-support/hello-handler)
      (let ([app (await (test-support/start-test-app app))])
        (let ([first-url (app/first-url app)])
          (let ([result (await (http/get (string-append first-url "/hello") (treelist)))])
            (check-equal? 200 (HttpResponse/status (unwrap result)))
            (check-equal? "hello world" (HttpResponse/body (unwrap result))))
          (test-support/shutdown-test-server app))))))
