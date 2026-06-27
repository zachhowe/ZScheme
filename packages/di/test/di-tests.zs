;; di-tests.zs — end-to-end DI: register, build a provider, resolve.
;;
;; This package can actually build a provider, so the tests close the loop the
;; di-abstractions suite cannot: a singleton resolved from the root provider, and a scoped
;; service resolved from a child scope's provider. The `: Greeter` annotations pin the
;; generic GetRequiredService<T> instantiation to T = Greeter.
(namespace ZScheme.DependencyInjection.Tests)
(module di-tests)

(import zunit)
(import di/provider)
(import di-abstractions/services)

;; A trivial service carrying a prefix.
(define-record Greeter [prefix : String])

(test-suite DiProviderTests
  ;; Resolve a pre-built singleton instance from the root provider.
  (test-case resolve_singleton_instance
    (let ([svcs (service-collection/new)])
      (services/add-singleton-instance svcs (typeof Greeter) (Greeter "hello"))
      (let* ([provider (services/build-provider svcs)]
             [g : Greeter (services/get-required-service provider)])
        (check-equal? "hello" (Greeter/prefix g)))))

  ;; Resolve a service produced by a singleton factory.
  (test-case resolve_singleton_factory
    (let ([svcs (service-collection/new)])
      (services/add-singleton-factory svcs (typeof Greeter)
        (lambda ([sp : System.IServiceProvider]) : System.Object (Greeter "hi")))
      (let* ([provider (services/build-provider svcs)]
             [g : Greeter (services/get-required-service provider)])
        (check-equal? "hi" (Greeter/prefix g)))))

  ;; Resolve a scoped service from a child scope's provider.
  (test-case resolve_scoped_in_scope
    (let ([svcs (service-collection/new)])
      (services/add-scoped-factory svcs (typeof Greeter)
        (lambda ([sp : System.IServiceProvider]) : System.Object (Greeter "scoped")))
      (let* ([provider (services/build-provider svcs)]
             [scope (service-provider/create-scope provider)]
             [g : Greeter (services/get-required-service (scope/services scope))])
        (check-equal? "scoped" (Greeter/prefix g))))))
