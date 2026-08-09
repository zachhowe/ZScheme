;; test-support.zs — Server lifecycle helpers for aspnet integration tests.
;;
;; Provides `start-test-server` which boots a WebApplication on a random port
;; (port 0) and blocks until the server is ready to accept connections.
;; `shutdown-test-server` gracefully stops the server.
(module test-support)

(import zunit)
(import stdlib/result)
(import stdlib/thread)
(import stdlib/treelist)
(import stdlib/datetime)
(import http)

(import aspnet/app)
(import aspnet/router)
(import aspnet/request)
(import aspnet/response)
(import aspnet/logging)

(import-clr
  Microsoft.AspNetCore.Http
  Microsoft.AspNetCore.Builder)

;; --- Server lifecycle ---

;; Check if the server accepts connections by making a quick GET request.
;; Returns #t on success, #f on any failure.
(define-async (test-support/check-ready [url : String])
  : (Task Bool)
  (let ([result (await (http/get url (treelist)))])
    (if (ok? result) #t #f)))

;; Internal: poll the server URL until it accepts connections.
;; #:recursive: an async poll must `await` itself, and `await` is never a tail position.
(define-async #:recursive (test-support/_poll-server [url : String] [deadline : Int])
  : Task
  (if (> (millis (now)) deadline)
      (fail "test server did not start within 10 seconds")
      (let ([ready (await (test-support/check-ready url))])
        (if ready
          (begin ())
          (begin
            (thread-sleep 100)
            (await (test-support/_poll-server url deadline)))))))

;; Poll the server URL until it accepts connections (max 10 seconds).
(define-async (test-support/wait-for-server [url : String])
  : Task
  (await (test-support/_poll-server url (+ (millis (now)) 10000))))

;; Build a test app without starting the server.
;; Routes and middleware should be registered before calling start-test-app.
(define (test-support/build-test-app)
  : WebApplication
  (let* ([builder (logging/clear-providers (app/create-builder))]
         [app (app/build builder)])
    (app/url-add app "http://127.0.0.1:0")
    app))

;; Start a test app that was built with build-test-app.
;; Waits until the server is ready to accept connections (max 10 seconds).
(define-async (test-support/start-test-app [app : WebApplication])
  : (Task WebApplication)
  (begin
    (await (app/start app))
    (await (test-support/wait-for-server (app/first-url app)))
    app))

;; Gracefully shut down a test server.
(define (test-support/shutdown-test-server [app : WebApplication])
  : Unit
  (app/shutdown app))

;; --- Default test handlers ---

;; A simple handler that writes a greeting.
(define-async (test-support/hello-handler [ctx : HttpContext])
  : Task
  (await (response/write-string ctx "hello world")))

;; A handler that writes a JSON response.
(define-async (test-support/json-handler [ctx : HttpContext])
  : Task
  (await (response/write-json ctx "{\"status\":\"ok\"}")))

;; Register the default handlers on an app (used when no custom handlers are provided).
(define (test-support/register-defaults [app : WebApplication])
  : Unit
  (route/get app "/hello" test-support/hello-handler)
  (route/get app "/json" test-support/json-handler))

(export test-support/build-test-app
        test-support/start-test-app
        test-support/shutdown-test-server
        test-support/wait-for-server
        test-support/check-ready
        test-support/hello-handler
        test-support/json-handler
        test-support/register-defaults
        treelist)
