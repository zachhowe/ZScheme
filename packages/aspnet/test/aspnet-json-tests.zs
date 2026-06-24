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
(import aspnet/request)
(import aspnet/response)
(import stdlib/json)
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

;; Exercises generic json/serialize<T>: the compiler resolves the concrete
;; instantiation from the record's type at the call site.
(define-record JsonWidget [name : String] [count : Int])

(define-async (handle-widget [ctx : HttpContext]) : Task
  (await (response/write-json ctx (json/serialize (JsonWidget "gadget" 7)))))

;; Exercises generic json/deserialize<T>: the result type (JsonWidget) is
;; inferred from how the deserialized value is used, then resolved positionally.
(define-async (handle-widget-echo [ctx : HttpContext]) : Task
  (let* ([body (await (request/read-body-string ctx))]
         [w (json/deserialize body)])
    (await (response/write-string ctx
            (string-append (JsonWidget/name w)
              (string-append ":" (json/serialize (JsonWidget/count w))))))))

;; ============================================================================
;; JSON Tests
;; ============================================================================

;; Test suite for JSON response handling.
(test-suite-async AspNetJsonTests
  (test-case-async write_json_sets_content_type
    (let ([app (test-support/build-test-app)])
      (route/get app "/json" test-support/json-handler)
      (let* ([app (await (test-support/start-test-app app))]
             [first-url (app/first-url app)])
        (let ([result (await (http/get (string-append first-url "/json") (treelist)))])
          (check-equal? 200 (HttpResponse/status (unwrap result)))
          (check-equal? "{\"status\":\"ok\"}" (HttpResponse/body (unwrap result))))
        (test-support/shutdown-test-server app))))

  (test-case-async write_json_with_complex_object
    (let ([app (test-support/build-test-app)])
      (route/get app "/complex" handle-json)
      (let* ([app (await (test-support/start-test-app app))]
             [first-url (app/first-url app)])
        (let ([result (await (http/get (string-append first-url "/complex") (treelist)))])
          (check-equal? 200 (HttpResponse/status (unwrap result)))
          (check-true (contains? (HttpResponse/body (unwrap result)) "test")))
        (test-support/shutdown-test-server app))))

  (test-case-async write_json_with_empty_object
    (let ([app (test-support/build-test-app)])
      (route/get app "/empty" handle-json-empty)
      (let* ([app (await (test-support/start-test-app app))]
             [first-url (app/first-url app)])
        (let ([result (await (http/get (string-append first-url "/empty") (treelist)))])
          (check-equal? 200 (HttpResponse/status (unwrap result)))
          (check-equal? "{}" (HttpResponse/body (unwrap result))))
        (test-support/shutdown-test-server app))))

  ;; Generic json/serialize over a user record produces the record's fields.
  (test-case-async serialize_record_with_generic_binding
    (let ([app (test-support/build-test-app)])
      (route/get app "/widget" handle-widget)
      (let* ([app (await (test-support/start-test-app app))]
             [first-url (app/first-url app)])
        (let ([result (await (http/get (string-append first-url "/widget") (treelist)))])
          (check-equal? 200 (HttpResponse/status (unwrap result)))
          (check-true (contains? (HttpResponse/body (unwrap result)) "gadget"))
          (check-true (contains? (HttpResponse/body (unwrap result)) "7")))
        (test-support/shutdown-test-server app))))

  ;; Generic json/deserialize reconstructs a real record from a posted body.
  (test-case-async deserialize_record_roundtrip
    (let ([app (test-support/build-test-app)])
      (route/post app "/widget/echo" handle-widget-echo)
      (let* ([app (await (test-support/start-test-app app))]
             [first-url (app/first-url app)])
        (let ([result (await (http/post-json (string-append first-url "/widget/echo")
                             "{\"Name\":\"gadget\",\"Count\":7}" (treelist)))])
          (check-equal? 200 (HttpResponse/status (unwrap result)))
          (check-equal? "gadget:7" (HttpResponse/body (unwrap result))))
        (test-support/shutdown-test-server app)))))
