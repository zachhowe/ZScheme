;; aspnet-cancellation-tests.zs — Integration tests for token-accepting lifecycle.
;;
;; Exercises app/start-with-token, app/shutdown-with-token and
;; app/run-async-with-token with a caller-supplied CancellationToken.
(namespace ZScheme.AspNet.Tests)
(module aspnet-cancellation-tests)

(import zunit)
(import http)
(import stdlib/concurrent/cancellation)
(import aspnet/app)
(import aspnet/router)
(import aspnet/response)
(import test-support)

(import-clr
  Microsoft.AspNetCore.Http
  Microsoft.AspNetCore.Builder)

(define-async (handle-ping [ctx : HttpContext]) : Task
  (await (response/write-string ctx "pong")))

(test-suite-async AspNetCancellationTests
  ;; Start with a caller's token, serve a request, then shut down with the token.
  (test-case-async start_and_shutdown_with_token
    (let [app (test-support/build-test-app)]
      (route/get app "/ping" handle-ping)
      (let [src (cancellation/new)]
        (begin
          (await (app/start-with-token app (cancellation/token src)))
          (await (test-support/wait-for-server (app/first-url app)))
          (let [first-url (app/first-url app)]
            (let [result (await (http/get (string-append first-url "/ping") (treelist)))]
              (check-equal? "pong" (HttpResponse/body (unwrap result)))))
          (app/shutdown-with-token app (cancellation/token src))
          (cancellation/dispose! src)))))

  ;; A timed-out source ends the awaited run task, so app/run-async-with-token
  ;; returns rather than blocking forever — verifies the IHost extension binding.
  (test-case-async run_async_with_token_stops_on_cancel
    (let [app (test-support/build-test-app)]
      (route/get app "/ping" handle-ping)
      (let [src (cancellation/new-with-timeout 500)]
        (begin
          (await (app/run-async-with-token app (cancellation/token src)))
          (check-true #t)
          (app/shutdown app)
          (cancellation/dispose! src))))))
