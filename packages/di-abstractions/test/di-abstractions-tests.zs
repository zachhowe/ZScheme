;; di-abstractions-tests.zs — smoke-tests the provider-agnostic DI bindings.
;;
;; Building a live provider and resolving services needs the concrete `di` package, so the
;; full register→build→resolve loop is covered there. What this suite guarantees is that the
;; bindings themselves are sound: every import-clr annotation is checked against the real CLR
;; member at compile time, and here each registration overload (singleton/scoped/transient
;; across type/self/instance/factory) actually runs against a fresh collection without
;; throwing. A successful run is the assertion.
(namespace ZScheme.DependencyInjection.Abstractions.Tests)
(module di-abstractions-tests)

(import zunit)
(import di-abstractions/services)

;; Trivial services used as registration keys/values.
(define-record Greeter [prefix : String])
(define-record Counter [n : Int])

;; A no-op factory matching the (IServiceProvider -> Object) shape the *-factory verbs take.
(define (make-greeter [sp : System.IServiceProvider]) : System.Object (Greeter "made"))

(test-suite DiAbstractionsTests
  ;; Every registration overload runs against a fresh collection.
  (test-case registration_verbs_run
    (let ([svcs (service-collection/new)])
      (services/add-singleton svcs (typeof Greeter) (typeof Greeter))
      (services/add-singleton-self svcs (typeof Counter))
      (services/add-singleton-instance svcs (typeof Greeter) (Greeter "hi"))
      (services/add-singleton-factory svcs (typeof Greeter) make-greeter)
      (services/add-scoped svcs (typeof Greeter) (typeof Greeter))
      (services/add-scoped-self svcs (typeof Counter))
      (services/add-scoped-factory svcs (typeof Greeter) make-greeter)
      (services/add-transient svcs (typeof Greeter) (typeof Greeter))
      (services/add-transient-self svcs (typeof Counter))
      (services/add-transient-factory svcs (typeof Greeter) make-greeter)
      ;; Reaching here means all ten bindings resolved and executed.
      (check-true #t))))
