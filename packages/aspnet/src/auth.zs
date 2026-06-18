;; auth.zs — authentication / authorization middleware factories
(module auth)

(import stdlib/string)
(import aspnet/request)
(import aspnet/response)

(import-clr
  ZScheme.AspNet.Bridge
  [auth/check-basic ZScheme.AspNet.Bridge.AuthBridge/CheckBasic
    : (String String String -> Bool)])

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

;; Build a middleware that requires Authorization: Basic <base64(user:pass)>.
;; On match: invokes next. On mismatch: 401 + plain-text body.
(define (auth/require-basic [user : String] [pass : String])
  : (Microsoft.AspNetCore.Http.HttpContext (-> Task) -> Task)
  (lambda ([ctx : Microsoft.AspNetCore.Http.HttpContext]
           [next : (-> Task)])
    (let [provided (request/header ctx "Authorization" "")]
      (if (auth/check-basic provided user pass)
          (next)
          (begin
            (response/status-set ctx 401)
            (response/write-string ctx "unauthorized"))))))

(export auth/require-bearer auth/require-basic)
