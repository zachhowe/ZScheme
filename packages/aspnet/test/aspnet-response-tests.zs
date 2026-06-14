;; aspnet-response-tests.zs — Response writer integration tests for the aspnet wrapper.
;;
;; Each test boots a WebApplication on a random port, sends real HTTP requests
;; using the http package client, and asserts on the responses. The server is
;; shut down after each test case.
(namespace ZScheme.AspNet.Tests)
(module aspnet-response-tests)

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
;; Response Test Handlers (top-level define-async)
;; ============================================================================

(define-async (handle-status [ctx : HttpContext]) : Task
  (begin
    (response/status-set ctx 201)
    (await (response/write-string ctx "created"))))

(define-async (handle-header [ctx : HttpContext]) : Task
  (begin
    (response/header-set ctx "X-Custom" "value")
    (await (response/write-string ctx "ok"))))

;; ============================================================================
;; Response Writer Tests
;; ============================================================================

;; Test suite for response writer functions.
(test-suite-async AspNetResponseTests
  (test-case-async status_code_can_be_set
    (let [app (test-support/build-test-app)]
      (route/post app "/create" handle-status)
      (let [app (await (test-support/start-test-app app))]
        (let [first-url (app/first-url app)]
          (let [result (await (http/post (string-append first-url "/create") "" "text/plain" (treelist)))]
            (begin
              (check-equal? 201 (HttpResponse/status (unwrap result)))
              (check-equal? "created" (HttpResponse/body (unwrap result)))))
          (test-support/shutdown-test-server app)))))

  (test-case-async response_header_can_be_set
    (let [app (test-support/build-test-app)]
      (route/get app "/header" handle-header)
      (let [app (await (test-support/start-test-app app))]
        (let [first-url (app/first-url app)]
          (let [result (await (http/get (string-append first-url "/header") (treelist)))]
            (begin
              (check-equal? 200 (HttpResponse/status (unwrap result)))
              (check-equal? "ok" (HttpResponse/body (unwrap result)))))
          (test-support/shutdown-test-server app))))))
