;; auth.zs — authentication / authorization middleware factories
(module auth)

(import stdlib/string)
(import aspnet/request)
(import aspnet/response)

;; Build a middleware that requires Authorization: Bearer <token>.
;; On match: invokes next. On mismatch: 401 + plain-text body.
(define (auth/require-bearer [token : String])
  : (Microsoft.AspNetCore.Http.HttpContext (-> Task) -> Task)
  (let [expected (string-append "Bearer " token)]
    (lambda ([ctx : Microsoft.AspNetCore.Http.HttpContext]
             [next : (-> Task)])
      (let [provided (request/header ctx "Authorization" "")]
        (if (equals? provided expected)
            (next)
            (begin
              (response/status-set ctx 401)
              (response/write-string ctx "unauthorized")))))))

(export auth/require-bearer)
