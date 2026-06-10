;; aspnet-tests.zs — Integration tests for the aspnet wrapper.
;;
;; Each test boots a WebApplication on a random port, sends real HTTP requests
;; using the http package client, and asserts on the responses. The server is
;; shut down after each test case.
(namespace ZScheme.AspNet.Tests)
(module aspnet-tests)

(import zunit)
(import stdlib/result)
(import http)
(import aspnet/app)
(import aspnet/router)
(import aspnet/request)
(import aspnet/response)
(import aspnet/middleware)
(import aspnet/auth)
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
        (let [result (http/get (string-append first-url "/hello") '())]
          (begin
            (check-equal? 200 (HttpResponse/status result))
            (check-equal? "hello world" (HttpResponse/body result)))))
      (test-support/shutdown-test-server app)))

  (test-case-async query_params_are_available
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (handle-search [ctx : HttpContext]) : Task
          (let [q (request/query ctx "q" "")]
            (await (response/write-string ctx (string-append "search: " q)))))
        (route/get app "/search" handle-search)
        (let [result1 (http/get (string-append first-url "/search?q=hello+world") '())]
          (begin
            (check-equal? 200 (HttpResponse/status result1))
            (check-equal? "search: hello world" (HttpResponse/body result1))))
        (let [result2 (http/get (string-append first-url "/search") '())]
          (begin
            (check-equal? 200 (HttpResponse/status result2))
            (check-equal? "search: " (HttpResponse/body result2)))))
      (test-support/shutdown-test-server app)))

  (test-case-async route_params_are_available
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (handle-user [ctx : HttpContext]) : Task
          (let [id (request/route-value ctx "id" "?")]
            (await (response/write-string ctx (string-append "user " id)))))
        (route/get app "/users/{id}" handle-user)
        (let [result1 (http/get (string-append first-url "/users/42") '())]
          (begin
            (check-equal? 200 (HttpResponse/status result1))
            (check-equal? "user 42" (HttpResponse/body result1))))
        (let [result2 (http/get (string-append first-url "/users/abc") '())]
          (begin
            (check-equal? 200 (HttpResponse/status result2))
            (check-equal? "user abc" (HttpResponse/body result2)))))
      (test-support/shutdown-test-server app)))

  (test-case-async post_with_body
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (handle-echo [ctx : HttpContext]) : Task
          (let [body (await (request/read-body-string ctx))]
            (await (response/write-json ctx body))))
        (route/post app "/echo" handle-echo)
        (let [result (http/post (string-append first-url "/echo") "hello" "text/plain" '())]
          (begin
            (check-equal? 200 (HttpResponse/status result))
            (check-equal? "\"hello\"" (HttpResponse/body result)))))
      (test-support/shutdown-test-server app)))

  (test-case-async post_with_json_body
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (handle-echo [ctx : HttpContext]) : Task
          (let [body (await (request/read-body-string ctx))]
            (await (response/write-json ctx body))))
        (route/post app "/echo" handle-echo)
        (let [result (http/post-json (string-append first-url "/echo") "{\"name\":\"test\"}" '())]
          (begin
            (check-equal? 200 (HttpResponse/status result))
            (check-equal? "{\"name\":\"test\"}" (HttpResponse/body result)))))
      (test-support/shutdown-test-server app)))

  (test-case-async put_method_works
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (handle-put [ctx : HttpContext]) : Task
          (let [body (await (request/read-body-string ctx))]
            (await (response/write-string ctx (string-append "updated: " body)))))
        (route/put app "/resource" handle-put)
        (let [result (http/put (string-append first-url "/resource") "data" "text/plain" '())]
          (begin
            (check-equal? 200 (HttpResponse/status result))
            (check-equal? "updated: data" (HttpResponse/body result)))))
      (test-support/shutdown-test-server app)))

  (test-case-async delete_method_works
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (handle-delete [ctx : HttpContext]) : Task
          (begin
            (response/status-set ctx 204)
            (await (response/write-string ctx ""))))
        (route/delete app "/resource/{id}" handle-delete)
        (let [result (http/delete (string-append first-url "/resource/5") '())]
          (begin
            (check-equal? 204 (HttpResponse/status result))
            (check-equal? "" (HttpResponse/body result)))))
      (test-support/shutdown-test-server app)))

  (test-case-async unknown_route_returns_404
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (route/get app "/exists" test-support/hello-handler)
        (let [result (http/get (string-append first-url "/does-not-exist") '())]
          (check-true (HttpResponse/status result) >= 400)))
      (test-support/shutdown-test-server app))))

;; ============================================================================
;; Middleware Tests
;; ============================================================================

;; Test suite for middleware chain execution.
(test-suite-async AspNetMiddlewareTests
  (test-case-async middleware_executes_before_handler
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (custom-middleware [ctx : HttpContext] [next : (-> Task)]) : Task
          (begin
            (response/header-set ctx "X-Custom-Header" "middleware-was-here")
            (await (next))))
        (app/use app custom-middleware)
        (route/get app "/hello" test-support/hello-handler)
        (let [result (http/get (string-append first-url "/hello") '())]
          (begin
            (check-equal? 200 (HttpResponse/status result))
            (check-equal? "hello world" (HttpResponse/body result)))))
      (test-support/shutdown-test-server app)))

  (test-case-async middleware_chains_multiple
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (header-middleware [ctx : HttpContext] [next : (-> Task)]) : Task
          (begin
            (response/header-set ctx "X-Middleware-1" "yes")
            (await (next))))
        (define-async (timing-middleware [ctx : HttpContext] [next : (-> Task)]) : Task
          (begin
            (response/header-set ctx "X-Middleware-2" "yes")
            (await (next))))
        (app/use app header-middleware)
        (app/use app timing-middleware)
        (route/get app "/hello" test-support/hello-handler)
        (let [result (http/get (string-append first-url "/hello") '())]
          (begin
            (check-equal? 200 (HttpResponse/status result))
            (check-equal? "hello world" (HttpResponse/body result)))))
      (test-support/shutdown-test-server app))))

;; ============================================================================
;; Auth Tests
;; ============================================================================

;; Test suite for authentication middleware.
(test-suite-async AspNetAuthTests
  (test-case-async unauthorized_without_token
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (protected-handler [ctx : HttpContext]) : Task
          (await (response/write-string ctx "secret")))
        (app/use app (auth/require-bearer "secret-token"))
        (route/get app "/protected" protected-handler)
        (let [result (http/get (string-append first-url "/protected") '())]
          (check-equal? 401 (HttpResponse/status result))))
      (test-support/shutdown-test-server app)))

  (test-case-async authorized_with_valid_token
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (protected-handler [ctx : HttpContext]) : Task
          (await (response/write-string ctx "secret")))
        (app/use app (auth/require-bearer "secret-token"))
        (route/get app "/protected" protected-handler)
        (let [headers (treelist-cons "Authorization" "Bearer secret-token" '())]
          (let [result (http/get (string-append first-url "/protected") headers)]
            (begin
              (check-equal? 200 (HttpResponse/status result))
              (check-equal? "secret" (HttpResponse/body result)))))
        (let [headers (treelist-cons "Authorization" "Bearer wrong-token" '())]
          (let [result (http/get (string-append first-url "/protected") headers)]
            (check-equal? 401 (HttpResponse/status result))))
        (let [headers (treelist-cons "Authorization" "Basic dXNlcjpwYXNz" '())]
          (let [result (http/get (string-append first-url "/protected") headers)]
            (check-equal? 401 (HttpResponse/status result))))
        (let [headers (treelist-cons "Authorization" "" '())]
          (let [result (http/get (string-append first-url "/protected") headers)]
            (check-equal? 401 (HttpResponse/status result))))
        (let [result (http/get (string-append first-url "/protected") '())]
          (check-equal? 401 (HttpResponse/status result))))
      (test-support/shutdown-test-server app))))

;; ============================================================================
;; JSON Tests
;; ============================================================================

;; Test suite for JSON response handling.
(test-suite-async AspNetJsonTests
  (test-case-async write_json_sets_content_type
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (route/get app "/json" test-support/json-handler)
        (let [result (http/get (string-append first-url "/json") '())]
          (begin
            (check-equal? 200 (HttpResponse/status result))
            (check-equal? "{\"status\":\"ok\"}" (HttpResponse/body result)))))
      (test-support/shutdown-test-server app)))

  (test-case-async write_json_with_complex_object
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (handle-json [ctx : HttpContext]) : Task
          (await (response/write-json ctx
                  "{\"name\":\"test\",\"count\":42,\"items\":[1,2,3]}")))
        (route/get app "/complex" handle-json)
        (let [result (http/get (string-append first-url "/complex") '())]
          (begin
            (check-equal? 200 (HttpResponse/status result))
            (check-true (HttpResponse/body result) contains? "test"))))
      (test-support/shutdown-test-server app)))

  (test-case-async write_json_with_empty_object
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (handle-json [ctx : HttpContext]) : Task
          (await (response/write-json ctx "{}")))
        (route/get app "/empty" handle-json)
        (let [result (http/get (string-append first-url "/empty") '())]
          (begin
            (check-equal? 200 (HttpResponse/status result))
            (check-equal? "{}" (HttpResponse/body result)))))
      (test-support/shutdown-test-server app))))

;; ============================================================================
;; Request Accessor Tests
;; ============================================================================

;; Test suite for request accessor functions.
(test-suite-async AspNetRequestTests
  (test-case-async request_method_is_correct
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (handle-method [ctx : HttpContext]) : Task
          (await (response/write-string ctx (request/method ctx))))
        (route/get app "/method" handle-method)
        (let [result (http/get (string-append first-url "/method") '())]
          (check-equal? "GET" (HttpResponse/body result))))
      (test-support/shutdown-test-server app)))

  (test-case-async request_path_is_correct
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (handle-path [ctx : HttpContext]) : Task
          (await (response/write-string ctx (request/path ctx))))
        (route/get app "/path" handle-path)
        (let [result (http/get (string-append first-url "/path") '())]
          (check-equal? "/path" (HttpResponse/body result))))
      (test-support/shutdown-test-server app)))

  (test-case-async request_header_is_available
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (handle-header [ctx : HttpContext]) : Task
          (await (response/write-string ctx (request/header ctx "X-Custom" "default"))))
        (route/get app "/header" handle-header)
        (let [headers (treelist-cons "X-Custom" "custom-value" '())]
          (let [result (http/get (string-append first-url "/header") headers)]
            (check-equal? "custom-value" (HttpResponse/body result))))
        (let [result (http/get (string-append first-url "/header") '())]
          (check-equal? "default" (HttpResponse/body result))))
      (test-support/shutdown-test-server app))))

;; ============================================================================
;; Response Writer Tests
;; ============================================================================

;; Test suite for response writer functions.
(test-suite-async AspNetResponseTests
  (test-case-async status_code_can_be_set
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (handle-status [ctx : HttpContext]) : Task
          (begin
            (response/status-set ctx 201)
            (await (response/write-string ctx "created"))))
        (route/post app "/create" handle-status)
        (let [result (http/post (string-append first-url "/create") "" "text/plain" '())]
          (begin
            (check-equal? 201 (HttpResponse/status result))
            (check-equal? "created" (HttpResponse/body result)))))
      (test-support/shutdown-test-server app)))

  (test-case-async response_header_can_be_set
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (handle-header [ctx : HttpContext]) : Task
          (begin
            (response/header-set ctx "X-Custom" "value")
            (await (response/write-string ctx "ok"))))
        (route/get app "/header" handle-header)
        (let [result (http/get (string-append first-url "/header") '())]
          (begin
            (check-equal? 200 (HttpResponse/status result))
            (check-equal? "ok" (HttpResponse/body result)))))
      (test-support/shutdown-test-server app))))

;; ============================================================================
;; Combined Scenario Tests
;; ============================================================================

;; Test suite for multi-feature scenarios.
(test-suite-async AspNetCombinedTests
  (test-case-async middleware_with_auth_and_routing
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (log-middleware [ctx : HttpContext] [next : (-> Task)]) : Task
          (begin
            (response/header-set ctx "X-Logged" "true")
            (await (next))))
        (define-async (protected-handler [ctx : HttpContext]) : Task
          (let [name (request/query ctx "name" "world")]
            (await (response/write-string ctx (string-append "hello " name)))))
        (app/use app log-middleware)
        (app/use app (auth/require-bearer "my-token"))
        (route/get app "/greet" protected-handler)
        (let [result (http/get (string-append first-url "/greet?name=world") '())]
          (check-equal? 401 (HttpResponse/status result)))
        (let [headers (treelist-cons "Authorization" "Bearer my-token" '())]
          (let [result (http/get (string-append first-url "/greet?name=world") headers)]
            (begin
              (check-equal? 200 (HttpResponse/status result))
              (check-equal? "hello world" (HttpResponse/body result)))))
        (let [headers (treelist-cons "Authorization" "Bearer wrong-token" '())]
          (let [result (http/get (string-append first-url "/greet?name=world") headers)]
            (check-equal? 401 (HttpResponse/status result)))))
      (test-support/shutdown-test-server app)))

  (test-case-async full_hello_world_app
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (define-async (log-middleware [ctx : HttpContext] [next : (-> Task)]) : Task
          (begin
            (response/header-set ctx "X-Logged" (request/method ctx))
            (await (next))))
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
        (app/use app log-middleware)
        (route/get app "/hello" handle-hello)
        (route/get app "/users/{id}" handle-user)
        (route/get app "/search" handle-search)
        (route/post app "/echo" handle-echo)
        (let [result1 (http/get (string-append first-url "/hello") '())]
          (check-equal? "hello world" (HttpResponse/body result1)))
        (let [result2 (http/get (string-append first-url "/users/42") '())]
          (check-equal? "user 42" (HttpResponse/body result2)))
        (let [result3 (http/get (string-append first-url "/search?q=test") '())]
          (check-equal? "search: test" (HttpResponse/body result3)))
        (let [result4 (http/post (string-append first-url "/echo") "hello" "text/plain" '())]
          (check-equal? "\"hello\"" (HttpResponse/body result4)))
        (let [result5 (http/post (string-append first-url "/echo") "{\"key\":\"val\"}" "application/json" '())]
          (check-equal? "{\"key\":\"val\"}" (HttpResponse/body result5))))
      (test-support/shutdown-test-server app))))
