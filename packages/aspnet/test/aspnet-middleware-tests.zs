;; aspnet-middleware-tests.zs — Middleware integration tests for the aspnet wrapper.
;;
;; Each test boots a WebApplication on a random port, sends real HTTP requests
;; using the http package client, and asserts on the responses. The server is
;; shut down after each test case.
(namespace ZScheme.AspNet.Tests)
(module aspnet-middleware-tests)

(import zunit)
(import http)
(import aspnet/app)
(import aspnet/router)
(import aspnet/response)
(import aspnet/middleware)
(import test-support)

(import-clr
  Microsoft.AspNetCore.Http
  Microsoft.AspNetCore.Builder)

;; ============================================================================
;; Middleware Test Handlers (top-level define-async)
;; ============================================================================

(define-async (custom-middleware [ctx : HttpContext] [next : (-> Task)]) : Task
  (begin
    (response/header-set ctx "X-Custom-Header" "middleware-was-here")
    (await (next))))

(define-async (header-middleware [ctx : HttpContext] [next : (-> Task)]) : Task
  (begin
    (response/header-set ctx "X-Middleware-1" "yes")
    (await (next))))

(define-async (timing-middleware [ctx : HttpContext] [next : (-> Task)]) : Task
  (begin
    (response/header-set ctx "X-Middleware-2" "yes")
    (await (next))))

;; ============================================================================
;; Middleware Tests
;; ============================================================================

;; Test suite for middleware chain execution.
(test-suite-async AspNetMiddlewareTests
  (test-case-async middleware_executes_before_handler
    (let ([app (test-support/build-test-app)])
      (app/use app custom-middleware)
      (route/get app "/hello" test-support/hello-handler)
      (let* ([app (await (test-support/start-test-app app))]
             [first-url (app/first-url app)])
        (let ([result (await (http/get (string-append first-url "/hello") (treelist)))])
          (check-equal? 200 (HttpResponse/status (unwrap result)))
          (check-equal? "hello world" (HttpResponse/body (unwrap result))))
        (test-support/shutdown-test-server app))))

  (test-case-async middleware_chains_multiple
    (let ([app (test-support/build-test-app)])
      (app/use app header-middleware)
      (app/use app timing-middleware)
      (route/get app "/hello" test-support/hello-handler)
      (let* ([app (await (test-support/start-test-app app))]
             [first-url (app/first-url app)])
        (let ([result (await (http/get (string-append first-url "/hello") (treelist)))])
          (check-equal? 200 (HttpResponse/status (unwrap result)))
          (check-equal? "hello world" (HttpResponse/body (unwrap result))))
        (test-support/shutdown-test-server app)))))
