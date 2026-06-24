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
;; Routing Test Handlers (top-level define-async)
;; ============================================================================

(define-async (handle-search [ctx : HttpContext]) : Task
  (let ([q (request/query ctx "q" "")])
    (await (response/write-string ctx (string-append "search: " q)))))

(define-async (handle-user [ctx : HttpContext]) : Task
  (let ([id (request/route-value ctx "id" "?")])
    (await (response/write-string ctx (string-append "user " id)))))

(define-async (handle-echo [ctx : HttpContext]) : Task
  (let ([body (await (request/read-body-string ctx))])
    (await (response/write-json ctx body))))

(define-async (handle-put [ctx : HttpContext]) : Task
  (let ([body (await (request/read-body-string ctx))])
    (await (response/write-string ctx (string-append "updated: " body)))))

(define-async (handle-delete [ctx : HttpContext]) : Task
  (begin
    (response/status-set ctx 204)
    (await (response/write-string ctx ""))))

;; Uses the typed helper request/query-int, which returns (Option Int).
(define-async (handle-count [ctx : HttpContext]) : Task
  (await (response/write-string ctx
          (match (request/query-int ctx "n")
            [(Some v) (string-append "n=" (int->string v))]
            [None "n=none"]))))

;; ============================================================================
;; Routing Tests
;; ============================================================================

;; Test suite for basic HTTP routing.
(test-suite-async AspNetRoutingTests
  (test-case-async get_returns_200_with_body
    (let ([app (test-support/build-test-app)])
      (route/get app "/hello" test-support/hello-handler)
      (let* ([app (await (test-support/start-test-app app))]
             [first-url (app/first-url app)])
        (let ([result (await (http/get (string-append first-url "/hello") (treelist)))])
          (check-equal? 200 (HttpResponse/status (unwrap result)))
          (check-equal? "hello world" (HttpResponse/body (unwrap result))))
        (test-support/shutdown-test-server app))))

  (test-case-async query_params_are_available
    (let ([app (test-support/build-test-app)])
      (route/get app "/search" handle-search)
      (let* ([app (await (test-support/start-test-app app))]
             [first-url (app/first-url app)])
        (let ([result1 (await (http/get (string-append first-url "/search?q=hello+world") (treelist)))])
          (check-equal? 200 (HttpResponse/status (unwrap result1)))
          (check-equal? "search: hello world" (HttpResponse/body (unwrap result1))))
        (let ([result2 (await (http/get (string-append first-url "/search") (treelist)))])
          (check-equal? 200 (HttpResponse/status (unwrap result2)))
          (check-equal? "search: " (HttpResponse/body (unwrap result2))))
        (test-support/shutdown-test-server app))))

  (test-case-async route_params_are_available
    (let ([app (test-support/build-test-app)])
      (route/get app "/users/{id}" handle-user)
      (let* ([app (await (test-support/start-test-app app))]
             [first-url (app/first-url app)])
        (let ([result1 (await (http/get (string-append first-url "/users/42") (treelist)))])
          (check-equal? 200 (HttpResponse/status (unwrap result1)))
          (check-equal? "user 42" (HttpResponse/body (unwrap result1))))
        (let ([result2 (await (http/get (string-append first-url "/users/abc") (treelist)))])
          (check-equal? 200 (HttpResponse/status (unwrap result2)))
          (check-equal? "user abc" (HttpResponse/body (unwrap result2))))
        (test-support/shutdown-test-server app))))

  (test-case-async query_int_parses_or_none
    (let ([app (test-support/build-test-app)])
      (route/get app "/count" handle-count)
      (let* ([app (await (test-support/start-test-app app))]
             [first-url (app/first-url app)])
        (let ([r1 (await (http/get (string-append first-url "/count?n=42") (treelist)))])
          (check-equal? "n=42" (HttpResponse/body (unwrap r1))))
        (let ([r2 (await (http/get (string-append first-url "/count?n=abc") (treelist)))])
          (check-equal? "n=none" (HttpResponse/body (unwrap r2))))
        (let ([r3 (await (http/get (string-append first-url "/count") (treelist)))])
          (check-equal? "n=none" (HttpResponse/body (unwrap r3))))
        (test-support/shutdown-test-server app))))

  (test-case-async post_with_body
    (let ([app (test-support/build-test-app)])
      (route/post app "/echo" handle-echo)
      (let* ([app (await (test-support/start-test-app app))]
             [first-url (app/first-url app)])
        (let ([result (await (http/post (string-append first-url "/echo") "hello" "text/plain" (treelist)))])
          (check-equal? 200 (HttpResponse/status (unwrap result)))
          (check-equal? "hello" (HttpResponse/body (unwrap result))))
        (test-support/shutdown-test-server app))))

  (test-case-async post_with_json_body
    (let ([app (test-support/build-test-app)])
      (route/post app "/echo" handle-echo)
      (let* ([app (await (test-support/start-test-app app))]
             [first-url (app/first-url app)])
        (let ([result (await (http/post-json (string-append first-url "/echo") "{\"name\":\"test\"}" (treelist)))])
          (check-equal? 200 (HttpResponse/status (unwrap result)))
          (check-equal? "{\"name\":\"test\"}" (HttpResponse/body (unwrap result))))
        (test-support/shutdown-test-server app))))

  (test-case-async put_method_works
    (let ([app (test-support/build-test-app)])
      (route/put app "/resource" handle-put)
      (let* ([app (await (test-support/start-test-app app))]
             [first-url (app/first-url app)])
        (let ([result (await (http/put (string-append first-url "/resource") "data" "text/plain" (treelist)))])
          (check-equal? 200 (HttpResponse/status (unwrap result)))
          (check-equal? "updated: data" (HttpResponse/body (unwrap result))))
        (test-support/shutdown-test-server app))))

  (test-case-async delete_method_works
    (let ([app (test-support/build-test-app)])
      (route/delete app "/resource/{id}" handle-delete)
      (let* ([app (await (test-support/start-test-app app))]
             [first-url (app/first-url app)])
        (let ([result (await (http/delete (string-append first-url "/resource/5") (treelist)))])
          (check-equal? 204 (HttpResponse/status (unwrap result)))
          (check-equal? "" (HttpResponse/body (unwrap result))))
        (test-support/shutdown-test-server app))))

  (test-case-async unknown_route_returns_404
    (let ([app (test-support/build-test-app)])
      (route/get app "/exists" test-support/hello-handler)
      (let* ([app (await (test-support/start-test-app app))]
             [first-url (app/first-url app)])
        (let ([result (await (http/get (string-append first-url "/does-not-exist") (treelist)))])
          (check-true (>= (HttpResponse/status (unwrap result)) 400)))
        (test-support/shutdown-test-server app)))))
