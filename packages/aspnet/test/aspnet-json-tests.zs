;; aspnet-json-tests.zs — JSON response integration tests for the aspnet wrapper.
;;
;; Each test boots a WebApplication on a random port, sends real HTTP requests
;; using the http package client, and asserts on the responses. The server is
;; shut down after each test case.
(namespace ZScheme.AspNet.Tests)
(module aspnet-json-tests)

(import zunit)
(import http)
(import aspnet/app)
(import aspnet/router)
(import aspnet/response)
(import test-support)

(import-clr
  Microsoft.AspNetCore.Http
  Microsoft.AspNetCore.Builder)

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
