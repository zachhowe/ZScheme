;; aspnet-combined-tests.zs — Combined scenario integration tests for the aspnet wrapper.
;;
;; Each test boots a WebApplication on a random port, sends real HTTP requests
;; using the http package client, and asserts on the responses. The server is
;; shut down after each test case.
(namespace ZScheme.AspNet.Tests)
(module aspnet-combined-tests)

(import zunit)
(import http)
(import aspnet/app)
(import aspnet/router)
(import aspnet/request)
(import aspnet/response)
(import aspnet/auth)
(import aspnet/middleware)
(import test-support)

(import-clr
  Microsoft.AspNetCore.Http
  Microsoft.AspNetCore.Builder)

;; ============================================================================
;; Combined Test Handlers (top-level define-async)
;; ============================================================================

(define-async (log-middleware [ctx : HttpContext] [next : (-> Task)]) : Task
  (begin
    (response/header-set ctx "X-Logged" "true")
    (await (next))))

(define-async (log-middleware-full [ctx : HttpContext] [next : (-> Task)]) : Task
  (begin
    (response/header-set ctx "X-Logged" (request/method ctx))
    (await (next))))

(define-async (protected-handler [ctx : HttpContext]) : Task
  (let [name (request/query ctx "name" "world")]
    (await (response/write-string ctx (string-append "hello " name)))))

(define-async (handle-hello [ctx : HttpContext]) : Task
  (await (response/write-string ctx "hello world")))

(define-async (handle-user [ctx : HttpContext]) : Task
  (let [id (request/route-value ctx "id" "?")]
    (await (response/write-string ctx (string-append "user " id)))))

(define-async (handle-search [ctx : HttpContext]) : Task
  (let [q (request/query ctx "q" "")]
    (await (response/write-string ctx (string-append "search: " q)))))

(define-async (handle-echo [ctx : HttpContext]) : Task
  (let [body (await (request/read-body-string ctx))]
    (await (response/write-json ctx body))))

;; ============================================================================
;; Combined Scenario Tests
;; ============================================================================

;; Test suite for multi-feature scenarios.
(test-suite-async AspNetCombinedTests
  (test-case-async middleware_with_auth_and_routing
    (let [app (test-support/build-test-app)]
      (app/use app log-middleware)
      (app/use app (auth/require-bearer "my-token"))
      (route/get app "/greet" protected-handler)
      (let [app (await (test-support/start-test-app app))]
        (let [first-url (app/first-url app)]
          (let [result (await (http/get (string-append first-url "/greet?name=world") (treelist)))]
            (check-equal? 401 (HttpResponse/status (unwrap result))))
          (let [headers (treelist (treelist "Authorization" "Bearer my-token"))]
            (let [result (await (http/get (string-append first-url "/greet?name=world") headers))]
              (begin
                (check-equal? 200 (HttpResponse/status (unwrap result)))
                (check-equal? "hello world" (HttpResponse/body (unwrap result))))))
          (let [headers (treelist (treelist "Authorization" "Bearer wrong-token"))]
            (let [result (await (http/get (string-append first-url "/greet?name=world") headers))]
              (check-equal? 401 (HttpResponse/status (unwrap result)))))
          (test-support/shutdown-test-server app)))))

  (test-case-async full_hello_world_app
    (let [app (test-support/build-test-app)]
      (app/use app log-middleware-full)
      (route/get app "/hello" handle-hello)
      (route/get app "/users/{id}" handle-user)
      (route/get app "/search" handle-search)
      (route/post app "/echo" handle-echo)
      (let [app (await (test-support/start-test-app app))]
        (let [first-url (app/first-url app)]
          (let [result1 (await (http/get (string-append first-url "/hello") (treelist)))]
            (check-equal? "hello world" (HttpResponse/body (unwrap result1))))
          (let [result2 (await (http/get (string-append first-url "/users/42") (treelist)))]
            (check-equal? "user 42" (HttpResponse/body (unwrap result2))))
          (let [result3 (await (http/get (string-append first-url "/search?q=test") (treelist)))]
            (check-equal? "search: test" (HttpResponse/body (unwrap result3))))
          (let [result4 (await (http/post (string-append first-url "/echo") "hello" "text/plain" (treelist)))]
            (check-equal? "hello" (HttpResponse/body (unwrap result4))))
          (let [result5 (await (http/post (string-append first-url "/echo") "{\"key\":\"val\"}" "application/json" (treelist)))]
            (check-equal? "{\"key\":\"val\"}" (HttpResponse/body (unwrap result5)))))
        (test-support/shutdown-test-server app)))))
