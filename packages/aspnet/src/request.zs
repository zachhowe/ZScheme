;; request.zs — HttpContext request accessors
(module request)

(import stdlib/option)

(import-clr
  Microsoft.AspNetCore.Http
  Microsoft.Extensions.Primitives
  System

  ;; HttpContext.Request : HttpRequest
  [req HttpContext.Request
    :instance-property : (HttpContext -> HttpRequest)]

  [req-method HttpRequest.Method
    :instance-property : (HttpRequest -> String)]

  ;; HttpRequest.Path : PathString (a struct); ToString() yields "" rather than
  ;; null for an empty path, so it is null-safe unlike the raw .Value.
  [req-path HttpRequest.Path
    :instance-property : (HttpRequest -> PathString)]
  [pathstring->string PathString.ToString
    :instance : (PathString -> String)]

  [req-query HttpRequest.Query
    :instance-property : (HttpRequest -> IQueryCollection)]
  ;; out-param surfaces as (ValueTuple Bool StringValues)
  [query-try-get IQueryCollection.TryGetValue
    :instance : (IQueryCollection String -> (ValueTuple Bool StringValues))]

  [req-headers HttpRequest.Headers
    :instance-property : (HttpRequest -> IHeaderDictionary)]
  [header-try-get IHeaderDictionary.TryGetValue
    :instance : (IHeaderDictionary String -> (ValueTuple Bool StringValues))]

  ;; RouteValueDictionary lives in Microsoft.AspNetCore.Http.Abstractions.dll
  ;; despite its Microsoft.AspNetCore.Routing namespace.
  [req-route-values HttpRequest.RouteValues
    :from "Microsoft.AspNetCore.Http.Abstractions"
    :instance-property : (HttpRequest -> Microsoft.AspNetCore.Routing.RouteValueDictionary)]
  ;; out-param surfaces as (ValueTuple Bool Object)
  [route-try-get Microsoft.AspNetCore.Routing.RouteValueDictionary.TryGetValue
    :from "Microsoft.AspNetCore.Http.Abstractions"
    :instance : (Microsoft.AspNetCore.Routing.RouteValueDictionary String
                 -> (ValueTuple Bool System.Object))]

  ;; StringValues.ToString() concatenates the values (single value -> that value).
  [stringvalues->string StringValues.ToString
    :instance : (StringValues -> String)]
  [object->string System.Object.ToString
    :instance : (System.Object -> String)]

  [req-body HttpRequest.Body
    :instance-property : (HttpRequest -> System.IO.Stream)]
  [read-to-end System.IO.StreamReader.ReadToEndAsync
    :instance : (System.IO.StreamReader -> (Task String))]

  ;; HttpContext.RequestServices : IServiceProvider — the per-request (scoped) provider.
  ;; Resolve scoped services from here, not from the app's root provider.
  [req-services HttpContext.RequestServices
    :instance-property : (HttpContext -> IServiceProvider)])

(define (request/method [ctx : HttpContext]) : String
  (req-method (req ctx)))

(define (request/path [ctx : HttpContext]) : String
  (pathstring->string (req-path (req ctx))))

;; Returns the route value for `key`, or `fallback` when absent.
(define (request/route-value [ctx : HttpContext]
                             [key : String] [fallback : String]) : String
  (match (route-try-get (req-route-values (req ctx)) key)
    [(values ok v) (if ok (object->string v) fallback)]))

;; Returns the query string value for `key`, or `fallback` when absent.
(define (request/query [ctx : HttpContext]
                       [key : String] [fallback : String]) : String
  (match (query-try-get (req-query (req ctx)) key)
    [(values ok v) (if ok (stringvalues->string v) fallback)]))

;; Returns the request header value for `name`, or `fallback` when absent.
(define (request/header [ctx : HttpContext]
                        [name : String] [fallback : String]) : String
  (match (header-try-get (req-headers (req ctx)) name)
    [(values ok v) (if ok (stringvalues->string v) fallback)]))

(define (request/read-body-string [ctx : HttpContext]) : (Task String)
  (let ([reader (new System.IO.StreamReader (req-body (req ctx)))])
    (read-to-end reader)))

;; The per-request (scoped) service provider — resolve services via
;; di-abstractions/services' services/get-required-service / services/get-service.
(define (request/services [ctx : HttpContext]) : IServiceProvider
  (req-services ctx))

(import-clr
  System
  ;; Int32.TryParse(string, out int) — out param surfaces as (ValueTuple Bool Int).
  [int-try-parse Int32/TryParse])

;; Parse a query-string value as an Int, returning None when absent or non-numeric.
(define (request/query-int [ctx : HttpContext] [key : String]) : (Option Int)
  (match (int-try-parse (request/query ctx key ""))
    [(values ok n) (if ok (Some n) None)]))

;; Parse a route value as an Int, returning None when absent or non-numeric.
(define (request/route-value-int [ctx : HttpContext] [key : String]) : (Option Int)
  (match (int-try-parse (request/route-value ctx key ""))
    [(values ok n) (if ok (Some n) None)]))

(export request/method request/path
        request/route-value request/query request/header
        request/read-body-string request/services
        request/query-int request/route-value-int)
