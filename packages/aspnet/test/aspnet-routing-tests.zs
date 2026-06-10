;; aspnet-routing-tests.zs — Routing integration tests for the aspnet wrapper.
;;
;; Each test boots a WebApplication on a random port, sends real HTTP requests
;; using the http package client, and asserts on the responses. The server is
;; shut down after each test case.
(namespace ZScheme.AspNet.Tests)
(module aspnet-routing-tests)

(import zunit)
(import http)
(import aspnet/app)
(import aspnet/router)
(import aspnet/request)
(import aspnet/response)
(import test-support)

(import-clr
  Microsoft.AspNetCore.Http
  Microsoft.AspNetCore.Builder)

;; ============================================================================
;; Routing Tests
;; ============================================================================

;; Test suite for basic HTTP routing.
(test-suite-async AspNetRoutingTests
  (test-case-async get_returns_200_with_body
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (route/get app "/hello" test-support/hello-handler)
        (let [result (await (http/get (string-append first-url "/hello") '()))]
          (begin
            (check-equal? 200 (HttpResponse/status (unwrap result)))
            (check-equal? "hello world" (HttpResponse/body (unwrap result))))))
      (test-support/shutdown-test-server app)))

  (test-case-async query_params_are_available
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (handle-search [ctx : HttpContext]) : Task
          (let [q (request/query ctx "q" "")]
            (await (response/write-string ctx (string-append "search: " q)))))
        (route/get app "/search" handle-search)
        (let [result1 (await (http/get (string-append first-url "/search?q=hello+world") '()))]
          (begin
            (check-equal? 200 (HttpResponse/status (unwrap result1)))
            (check-equal? "search: hello world" (HttpResponse/body (unwrap result1)))))
        (let [result2 (await (http/get (string-append first-url "/search") '()))]
          (begin
            (check-equal? 200 (HttpResponse/status (unwrap result2)))
            (check-equal? "search: " (HttpResponse/body (unwrap result2))))))
      (test-support/shutdown-test-server app)))

  (test-case-async route_params_are_available
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (handle-user [ctx : HttpContext]) : Task
          (let [id (request/route-value ctx "id" "?")]
            (await (response/write-string ctx (string-append "user " id)))))
        (route/get app "/users/{id}" handle-user)
        (let [result1 (await (http/get (string-append first-url "/users/42") '()))]
          (begin
            (check-equal? 200 (HttpResponse/status (unwrap result1)))
            (check-equal? "user 42" (HttpResponse/body (unwrap result1)))))
        (let [result2 (await (http/get (string-append first-url "/users/abc") '()))]
          (begin
            (check-equal? 200 (HttpResponse/status (unwrap result2)))
            (check-equal? "user abc" (HttpResponse/body (unwrap result2))))))
      (test-support/shutdown-test-server app)))

  (test-case-async post_with_body
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (handle-echo [ctx : HttpContext]) : Task
          (let [body (await (request/read-body-string ctx))]
            (await (response/write-json ctx body))))
        (route/post app "/echo" handle-echo)
        (let [result (await (http/post (string-append first-url "/echo") "hello" "text/plain" '()))]
          (begin
            (check-equal? 200 (HttpResponse/status (unwrap result)))
            (check-equal? "\"hello\"" (HttpResponse/body (unwrap result))))))
      (test-support/shutdown-test-server app)))

  (test-case-async post_with_json_body
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (handle-echo [ctx : HttpContext]) : Task
          (let [body (await (request/read-body-string ctx))]
            (await (response/write-json ctx body))))
        (route/post app "/echo" handle-echo)
        (let [result (await (http/post-json (string-append first-url "/echo") "{\"name\":\"test\"}" '()))]
          (begin
            (check-equal? 200 (HttpResponse/status (unwrap result)))
            (check-equal? "{\"name\":\"test\"}" (HttpResponse/body (unwrap result))))))
      (test-support/shutdown-test-server app)))

  (test-case-async put_method_works
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (handle-put [ctx : HttpContext]) : Task
          (let [body (await (request/read-body-string ctx))]
            (await (response/write-string ctx (string-append "updated: " body)))))
        (route/put app "/resource" handle-put)
        (let [result (await (http/put (string-append first-url "/resource") "data" "text/plain" '()))]
          (begin
            (check-equal? 200 (HttpResponse/status (unwrap result)))
            (check-equal? "updated: data" (HttpResponse/body (unwrap result))))))
      (test-support/shutdown-test-server app)))

  (test-case-async delete_method_works
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (handle-delete [ctx : HttpContext]) : Task
          (begin
            (response/status-set ctx 204)
            (await (response/write-string ctx ""))))
        (route/delete app "/resource/{id}" handle-delete)
        (let [result (await (http/delete (string-append first-url "/resource/5") '()))]
          (begin
            (check-equal? 204 (HttpResponse/status (unwrap result)))
            (check-equal? "" (HttpResponse/body (unwrap result))))))
      (test-support/shutdown-test-server app)))

  (test-case-async unknown_route_returns_404
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (route/get app "/exists" test-support/hello-handler)
        (let [result (await (http/get (string-append first-url "/does-not-exist") '()))]
          (check-true (>= (HttpResponse/status (unwrap result)) 400))))
      (test-support/shutdown-test-server app))))
