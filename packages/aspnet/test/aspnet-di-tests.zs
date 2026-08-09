;; aspnet-di-tests.zs — Dependency-injection integration tests for the aspnet wrapper.
;;
;; Each test registers a service on builder.Services BEFORE app/build (so it cannot use
;; test-support/build-test-app, which builds internally), boots the app on a random port,
;; and asserts that a handler can resolve the service from request/services over real HTTP.
(namespace ZScheme.AspNet.Tests)
(module aspnet-di-tests)

(import zunit)
(import http)
(import aspnet/app)
(import aspnet/router)
(import aspnet/request)
(import aspnet/response)
(import aspnet/services)
(import aspnet/logging)
(import di-abstractions/services)
(import test-support)

(import-clr
  Microsoft.AspNetCore.Http
  Microsoft.AspNetCore.Builder)

;; A trivial service: a greeter carrying a prefix.
(define-record Greeter [prefix : String])

;; Handler resolves Greeter from the request's scoped provider. The `: Greeter` annotation
;; pins the generic GetRequiredService<T> instantiation to T = Greeter.
(define-async (handle-greet [ctx : HttpContext]) : Task
  (let ([g : Greeter (services/get-required-service (request/services ctx))])
    (await (response/write-string ctx (string-append (Greeter/prefix g) " world")))))

;; Build an app with a Greeter registered via a pre-built instance (Singleton).
(define (build-instance-app) : WebApplication
  (let ([builder (logging/clear-providers (app/create-builder))])
    (services/add-singleton-instance (services/builder-services builder)
                                     (typeof Greeter) (Greeter "hello"))
    (let ([app (app/build builder)])
      (app/url-add app "http://127.0.0.1:0")
      app)))

;; Build an app with a Greeter produced by a registered factory (Singleton).
(define (build-factory-app) : WebApplication
  (let ([builder (logging/clear-providers (app/create-builder))])
    (services/add-singleton-factory (services/builder-services builder)
      (typeof Greeter)
      (lambda ([sp : System.IServiceProvider]) : System.Object (Greeter "hi")))
    (let ([app (app/build builder)])
      (app/url-add app "http://127.0.0.1:0")
      app)))

(test-suite-async AspNetDiTests
  ;; Resolve a service registered as a pre-built singleton instance.
  (test-case-async resolve_singleton_instance_in_handler
    (let ([app (build-instance-app)])
      (route/get app "/greet" handle-greet)
      (let* ([app (await (test-support/start-test-app app))]
             [first-url (app/first-url app)])
        (let ([result (await (http/get (string-append first-url "/greet") (treelist)))])
          (check-equal? 200 (HttpResponse/status (unwrap result)))
          (check-equal? "hello world" (HttpResponse/body (unwrap result))))
        (test-support/shutdown-test-server app))))

  ;; Resolve a service produced by a registered factory function.
  (test-case-async resolve_singleton_factory_in_handler
    (let ([app (build-factory-app)])
      (route/get app "/greet" handle-greet)
      (let* ([app (await (test-support/start-test-app app))]
             [first-url (app/first-url app)])
        (let ([result (await (http/get (string-append first-url "/greet") (treelist)))])
          (check-equal? 200 (HttpResponse/status (unwrap result)))
          (check-equal? "hi world" (HttpResponse/body (unwrap result))))
        (test-support/shutdown-test-server app)))))
