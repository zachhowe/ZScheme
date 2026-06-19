;; request.zs — HttpContext request accessors
(module request)

(import stdlib/option)

(import-clr
  Microsoft.AspNetCore.Http
  Microsoft.Extensions.Primitives

  ;; HttpContext.Request : HttpRequest
  [req Microsoft.AspNetCore.Http.HttpContext.Request
    :instance-property : (Microsoft.AspNetCore.Http.HttpContext
                          -> Microsoft.AspNetCore.Http.HttpRequest)]

  [req-method Microsoft.AspNetCore.Http.HttpRequest.Method
    :instance-property : (Microsoft.AspNetCore.Http.HttpRequest -> String)]

  ;; HttpRequest.Path : PathString (a struct); ToString() yields "" rather than
  ;; null for an empty path, so it is null-safe unlike the raw .Value.
  [req-path Microsoft.AspNetCore.Http.HttpRequest.Path
    :instance-property : (Microsoft.AspNetCore.Http.HttpRequest
                          -> Microsoft.AspNetCore.Http.PathString)]
  [pathstring->string Microsoft.AspNetCore.Http.PathString.ToString
    :instance : (Microsoft.AspNetCore.Http.PathString -> String)]

  [req-query Microsoft.AspNetCore.Http.HttpRequest.Query
    :instance-property : (Microsoft.AspNetCore.Http.HttpRequest
                          -> Microsoft.AspNetCore.Http.IQueryCollection)]
  ;; out-param surfaces as (ValueTuple Bool StringValues)
  [query-try-get Microsoft.AspNetCore.Http.IQueryCollection.TryGetValue
    :instance : (Microsoft.AspNetCore.Http.IQueryCollection String
                 -> (ValueTuple Bool Microsoft.Extensions.Primitives.StringValues))]

  [req-headers Microsoft.AspNetCore.Http.HttpRequest.Headers
    :instance-property : (Microsoft.AspNetCore.Http.HttpRequest
                          -> Microsoft.AspNetCore.Http.IHeaderDictionary)]
  [header-try-get Microsoft.AspNetCore.Http.IHeaderDictionary.TryGetValue
    :instance : (Microsoft.AspNetCore.Http.IHeaderDictionary String
                 -> (ValueTuple Bool Microsoft.Extensions.Primitives.StringValues))]

  ;; RouteValueDictionary lives in Microsoft.AspNetCore.Http.Abstractions.dll
  ;; despite its Microsoft.AspNetCore.Routing namespace.
  [req-route-values Microsoft.AspNetCore.Http.HttpRequest.RouteValues
    :from "Microsoft.AspNetCore.Http.Abstractions"
    :instance-property : (Microsoft.AspNetCore.Http.HttpRequest
                          -> Microsoft.AspNetCore.Routing.RouteValueDictionary)]
  ;; out-param surfaces as (ValueTuple Bool Object)
  [route-try-get Microsoft.AspNetCore.Routing.RouteValueDictionary.TryGetValue
    :from "Microsoft.AspNetCore.Http.Abstractions"
    :instance : (Microsoft.AspNetCore.Routing.RouteValueDictionary String
                 -> (ValueTuple Bool System.Object))]

  ;; StringValues.ToString() concatenates the values (single value -> that value).
  [stringvalues->string Microsoft.Extensions.Primitives.StringValues.ToString
    :instance : (Microsoft.Extensions.Primitives.StringValues -> String)]
  [object->string System.Object.ToString
    :instance : (System.Object -> String)]

  [req-body Microsoft.AspNetCore.Http.HttpRequest.Body
    :instance-property : (Microsoft.AspNetCore.Http.HttpRequest -> System.IO.Stream)]
  [read-to-end System.IO.StreamReader.ReadToEndAsync
    :instance : (System.IO.StreamReader -> (Task String))]

  ;; HttpContext.RequestServices : IServiceProvider — the per-request (scoped) provider.
  ;; Resolve scoped services from here, not from the app's root provider.
  [req-services Microsoft.AspNetCore.Http.HttpContext.RequestServices
    :instance-property : (Microsoft.AspNetCore.Http.HttpContext -> System.IServiceProvider)])

(define (request/method [ctx : Microsoft.AspNetCore.Http.HttpContext]) : String
  (req-method (req ctx)))

(define (request/path [ctx : Microsoft.AspNetCore.Http.HttpContext]) : String
  (pathstring->string (req-path (req ctx))))

;; Returns the route value for `key`, or `fallback` when absent.
(define (request/route-value [ctx : Microsoft.AspNetCore.Http.HttpContext]
                             [key : String] [fallback : String]) : String
  (match (route-try-get (req-route-values (req ctx)) key)
    [(values ok v) (if ok (object->string v) fallback)]))

;; Returns the query string value for `key`, or `fallback` when absent.
(define (request/query [ctx : Microsoft.AspNetCore.Http.HttpContext]
                       [key : String] [fallback : String]) : String
  (match (query-try-get (req-query (req ctx)) key)
    [(values ok v) (if ok (stringvalues->string v) fallback)]))

;; Returns the request header value for `name`, or `fallback` when absent.
(define (request/header [ctx : Microsoft.AspNetCore.Http.HttpContext]
                        [name : String] [fallback : String]) : String
  (match (header-try-get (req-headers (req ctx)) name)
    [(values ok v) (if ok (stringvalues->string v) fallback)]))

(define (request/read-body-string [ctx : Microsoft.AspNetCore.Http.HttpContext]) : (Task String)
  (let [reader (new System.IO.StreamReader (req-body (req ctx)))]
    (read-to-end reader)))

;; The per-request (scoped) service provider — resolve services via
;; aspnet/services' services/get-required-service / services/get-service.
(define (request/services [ctx : Microsoft.AspNetCore.Http.HttpContext]) : System.IServiceProvider
  (req-services ctx))

(import-clr
  System
  ;; Int32.TryParse(string, out int) — out param surfaces as (ValueTuple Bool Int).
  [int-try-parse System.Int32/TryParse])

;; Parse a query-string value as an Int, returning None when absent or non-numeric.
(define (request/query-int [ctx : Microsoft.AspNetCore.Http.HttpContext] [key : String]) : (Option Int)
  (match (int-try-parse (request/query ctx key ""))
    [(values ok n) (if ok (Some n) None)]))

;; Parse a route value as an Int, returning None when absent or non-numeric.
(define (request/route-value-int [ctx : Microsoft.AspNetCore.Http.HttpContext] [key : String]) : (Option Int)
  (match (int-try-parse (request/route-value ctx key ""))
    [(values ok n) (if ok (Some n) None)]))

(export request/method request/path
        request/route-value request/query request/header
        request/read-body-string request/services
        request/query-int request/route-value-int
        Option Some None)
