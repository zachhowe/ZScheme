;; di-abstractions-tests.zs — exercises the provider-agnostic DI bindings.
;;
;; Building a live provider and resolving services needs the concrete `di` package, so the
;; full register→build→resolve loop is covered there. Here we register services and observe
;; the collection grow via `service-collection/count` — which reads IServiceCollection.Count,
;; a property inherited from the closed generic ICollection<ServiceDescriptor>; binding it
;; exercises the compiler's inherited-interface-property support end-to-end.
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
  ;; A new collection has no registrations.
  (test-case new_collection_is_empty
    (check-equal? 0 (service-collection/count (service-collection/new))))

  ;; Every registration overload appends a descriptor.
  (test-case registration_verbs_grow_the_collection
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
      (check-equal? 10 (service-collection/count svcs)))))
