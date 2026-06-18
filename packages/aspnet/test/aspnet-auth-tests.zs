;; aspnet-auth-tests.zs — Authentication integration tests for the aspnet wrapper.
;;
;; Each test boots a WebApplication on a random port, sends real HTTP requests
;; using the http package client, and asserts on the responses. The server is
;; shut down after each test case.
(namespace ZScheme.AspNet.Tests)
(module aspnet-auth-tests)

(import zunit)
(import http)
(import aspnet/app)
(import aspnet/router)
(import aspnet/response)
(import aspnet/auth)
(import aspnet/middleware)
(import test-support)

(import-clr
  Microsoft.AspNetCore.Http
  Microsoft.AspNetCore.Builder)

;; ============================================================================
;; Auth Test Handlers (top-level define-async)
;; ============================================================================

(define-async (protected-handler [ctx : HttpContext]) : Task
  (await (response/write-string ctx "secret")))

;; ============================================================================
;; Auth Tests
;; ============================================================================

;; Test suite for authentication middleware.
(test-suite-async AspNetAuthTests
  (test-case-async unauthorized_without_token
    (let [app (test-support/build-test-app)]
      (app/use app (auth/require-bearer "secret-token"))
      (route/get app "/protected" protected-handler)
      (let [app (await (test-support/start-test-app app))]
        (let [first-url (app/first-url app)]
          (let [result (await (http/get (string-append first-url "/protected") (treelist)))]
            (check-equal? 401 (HttpResponse/status (unwrap result))))
          (test-support/shutdown-test-server app)))))

  (test-case-async authorized_with_valid_token
    (let [app (test-support/build-test-app)]
      (app/use app (auth/require-bearer "secret-token"))
      (route/get app "/protected" protected-handler)
      (let [app (await (test-support/start-test-app app))]
        (let [first-url (app/first-url app)]
          (let [headers (treelist (treelist "Authorization" "Bearer secret-token"))]
            (let [result (await (http/get (string-append first-url "/protected") headers))]
              (begin
                (check-equal? 200 (HttpResponse/status (unwrap result)))
                (check-equal? "secret" (HttpResponse/body (unwrap result))))))
          (let [headers (treelist (treelist "Authorization" "Bearer wrong-token"))]
            (let [result (await (http/get (string-append first-url "/protected") headers))]
              (check-equal? 401 (HttpResponse/status (unwrap result)))))
          (let [headers (treelist (treelist "Authorization" "Basic dXNlcjpwYXNz"))]
            (let [result (await (http/get (string-append first-url "/protected") headers))]
              (check-equal? 401 (HttpResponse/status (unwrap result)))))
          (let [headers (treelist (treelist "Authorization" ""))]
            (let [result (await (http/get (string-append first-url "/protected") headers))]
              (check-equal? 401 (HttpResponse/status (unwrap result)))))
          (let [result (await (http/get (string-append first-url "/protected") (treelist)))]
             (check-equal? 401 (HttpResponse/status (unwrap result))))
          (test-support/shutdown-test-server app)))))

  ;; require-basic accepts a valid Basic header (base64 of "user:pass") and
  ;; rejects wrong/missing credentials.
  (test-case-async basic_auth_gate
    (let [app (test-support/build-test-app)]
      (app/use app (auth/require-basic "user" "pass"))
      (route/get app "/protected" protected-handler)
      (let [app (await (test-support/start-test-app app))]
        (let [first-url (app/first-url app)]
          (let [headers (treelist (treelist "Authorization" "Basic dXNlcjpwYXNz"))]
            (let [result (await (http/get (string-append first-url "/protected") headers))]
              (begin
                (check-equal? 200 (HttpResponse/status (unwrap result)))
                (check-equal? "secret" (HttpResponse/body (unwrap result))))))
          (let [headers (treelist (treelist "Authorization" "Basic d3Jvbmc6Y3JlZHM="))]
            (let [result (await (http/get (string-append first-url "/protected") headers))]
              (check-equal? 401 (HttpResponse/status (unwrap result)))))
          (let [result (await (http/get (string-append first-url "/protected") (treelist)))]
            (check-equal? 401 (HttpResponse/status (unwrap result))))
          (test-support/shutdown-test-server app))))))
