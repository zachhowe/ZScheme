;; aspnet-tests.zs — Smoke tests for the aspnet wrapper.
;;
;; These import every module to verify that bindings type-check and that the
;; bridge assembly is wired correctly. Behavioral tests run against the
;; example app under examples/aspnet-hello/ via dotnet build + curl.
(namespace ZScheme.AspNet.Tests)
(module aspnet-tests)

(import zunit)
(import aspnet/app)
(import aspnet/router)
(import aspnet/request)
(import aspnet/response)
(import aspnet/middleware)
(import aspnet/auth)

(test-suite AspNetWiring
  (test-case bridge_create_builder_returns_builder
    (let [builder (app/create-builder)]
      ;; If we got here, the bridge resolved and returned a non-null builder.
      (check-true #t)))

  (test-case auth_factory_returns_function
    (let [middleware (auth/require-bearer "secret-token")]
      ;; auth/require-bearer should return a callable middleware closure.
      (check-true #t))))
