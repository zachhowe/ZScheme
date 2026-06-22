;; aspnet-request-tests.zs — Request accessor integration tests for the aspnet wrapper.
;;
;; Each test boots a WebApplication on a random port, sends real HTTP requests
;; using the http package client, and asserts on the responses. The server is
;; shut down after each test case.
(namespace ZScheme.AspNet.Tests)
(module aspnet-request-tests)

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
;; Request Test Handlers (top-level define-async)
;; ============================================================================

(define-async (handle-method [ctx : HttpContext]) : Task
  (await (response/write-string ctx (request/method ctx))))

(define-async (handle-path [ctx : HttpContext]) : Task
  (await (response/write-string ctx (request/path ctx))))

(define-async (handle-header [ctx : HttpContext]) : Task
  (await (response/write-string ctx (request/header ctx "X-Custom" "default"))))

;; ============================================================================
;; Request Accessor Tests
;; ============================================================================

;; Test suite for request accessor functions.
(test-suite-async AspNetRequestTests
  (test-case-async request_method_is_correct
    (let ([app (test-support/build-test-app)])
      (route/get app "/method" handle-method)
      (let ([app (await (test-support/start-test-app app))])
        (let ([first-url (app/first-url app)])
          (let ([result (await (http/get (string-append first-url "/method") (treelist)))])
            (check-equal? "GET" (HttpResponse/body (unwrap result)))))
        (test-support/shutdown-test-server app))))

  (test-case-async request_path_is_correct
    (let ([app (test-support/build-test-app)])
      (route/get app "/path" handle-path)
      (let ([app (await (test-support/start-test-app app))])
        (let ([first-url (app/first-url app)])
          (let ([result (await (http/get (string-append first-url "/path") (treelist)))])
            (check-equal? "/path" (HttpResponse/body (unwrap result)))))
        (test-support/shutdown-test-server app))))

  (test-case-async request_header_is_available
    (let ([app (test-support/build-test-app)])
      (route/get app "/header" handle-header)
      (let ([app (await (test-support/start-test-app app))])
        (let ([first-url (app/first-url app)])
          (let ([headers (treelist (treelist "X-Custom" "custom-value"))])
            (let ([result (await (http/get (string-append first-url "/header") headers))])
              (check-equal? "custom-value" (HttpResponse/body (unwrap result)))))
          (let ([result (await (http/get (string-append first-url "/header") (treelist)))])
            (check-equal? "default" (HttpResponse/body (unwrap result))))
          (test-support/shutdown-test-server app))))))
