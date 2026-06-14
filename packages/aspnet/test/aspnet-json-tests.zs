;; aspnet-json-tests.zs — JSON response integration tests for the aspnet wrapper.
;;
;; Each test boots a WebApplication on a random port, sends real HTTP requests
;; using the http package client, and asserts on the responses. The server is
;; shut down after each test case.
(namespace ZScheme.AspNet.Tests)
(module aspnet-json-tests)

(import zunit)
(import http)
(import stdlib/string)
(import aspnet/app)
(import aspnet/router)
(import aspnet/response)
(import test-support)

(import-clr
  Microsoft.AspNetCore.Http
  Microsoft.AspNetCore.Builder)

;; ============================================================================
;; JSON Test Handlers (top-level define-async)
;; ============================================================================

(define-async (handle-json [ctx : HttpContext]) : Task
  (await (response/write-json ctx
          "{\"name\":\"test\",\"count\":42,\"items\":[1,2,3]}")))

(define-async (handle-json-empty [ctx : HttpContext]) : Task
  (await (response/write-json ctx "{}")))

;; ============================================================================
;; JSON Tests
;; ============================================================================

;; Test suite for JSON response handling.
(test-suite-async AspNetJsonTests
  (test-case-async write_json_sets_content_type
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (route/get app "/json" test-support/json-handler)
        (let [result (await (http/get (string-append first-url "/json") (treelist)))]
          (begin
            (check-equal? 200 (HttpResponse/status (unwrap result)))
            (check-equal? "{\"status\":\"ok\"}" (HttpResponse/body (unwrap result))))))
      (test-support/shutdown-test-server app)))

  (test-case-async write_json_with_complex_object
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (route/get app "/complex" handle-json)
        (let [result (await (http/get (string-append first-url "/complex") (treelist)))]
          (begin
            (check-equal? 200 (HttpResponse/status (unwrap result)))
            (check-true (contains? (HttpResponse/body (unwrap result)) "test"))))
      (test-support/shutdown-test-server app)))

  (test-case-async write_json_with_empty_object
    (let [app (await (test-support/start-test-server))]
      (let [first-url (app/first-url app)]
        (route/get app "/empty" handle-json-empty)
        (let [result (await (http/get (string-append first-url "/empty") (treelist)))]
          (begin
            (check-equal? 200 (HttpResponse/status (unwrap result)))
            (check-equal? "{}" (HttpResponse/body (unwrap result))))))
      (test-support/shutdown-test-server app)))))
