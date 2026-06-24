;; router.zs — HTTP route registration (GET/POST/PUT/PATCH/DELETE)
(module router)

;; Bind directly to the framework's EndpointRouteBuilderExtensions map methods.
;; They live in the Microsoft.AspNetCore.Builder namespace but ship in
;; Microsoft.AspNetCore.Routing.dll, hence the :from hint. The (delegate
;; RequestDelegate) annotation selects the raw RequestDelegate overload (not the
;; minimal-API Delegate overload, which would JSON-bind the handler's parameter)
;; and coerces the ZScheme handler into a RequestDelegate.
(import-clr
  Microsoft.AspNetCore.Builder
  Microsoft.AspNetCore.Http
  Microsoft.AspNetCore.Routing

  [clr-map-get Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions/MapGet
    :from "Microsoft.AspNetCore.Routing"
    : (Microsoft.AspNetCore.Routing.IEndpointRouteBuilder String
       (delegate Microsoft.AspNetCore.Http.RequestDelegate)
       -> Microsoft.AspNetCore.Builder.IEndpointConventionBuilder)]

  [clr-map-post Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions/MapPost
    :from "Microsoft.AspNetCore.Routing"
    : (Microsoft.AspNetCore.Routing.IEndpointRouteBuilder String
       (delegate Microsoft.AspNetCore.Http.RequestDelegate)
       -> Microsoft.AspNetCore.Builder.IEndpointConventionBuilder)]

  [clr-map-put Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions/MapPut
    :from "Microsoft.AspNetCore.Routing"
    : (Microsoft.AspNetCore.Routing.IEndpointRouteBuilder String
       (delegate Microsoft.AspNetCore.Http.RequestDelegate)
       -> Microsoft.AspNetCore.Builder.IEndpointConventionBuilder)]

  [clr-map-patch Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions/MapPatch
    :from "Microsoft.AspNetCore.Routing"
    : (Microsoft.AspNetCore.Routing.IEndpointRouteBuilder String
       (delegate Microsoft.AspNetCore.Http.RequestDelegate)
       -> Microsoft.AspNetCore.Builder.IEndpointConventionBuilder)]

  [clr-map-delete Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions/MapDelete
    :from "Microsoft.AspNetCore.Routing"
    : (Microsoft.AspNetCore.Routing.IEndpointRouteBuilder String
       (delegate Microsoft.AspNetCore.Http.RequestDelegate)
       -> Microsoft.AspNetCore.Builder.IEndpointConventionBuilder)])

;; WebApplication implements IEndpointRouteBuilder; upcast then register, discarding
;; the returned IEndpointConventionBuilder so the public surface stays `-> Unit`.
(define (route/get [app : Microsoft.AspNetCore.Builder.WebApplication] [pattern : String]
                   [handler : (Microsoft.AspNetCore.Http.HttpContext -> Task)]) : Unit
  (let ([erb : Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app])
    (clr-map-get erb pattern handler) ()))

(define (route/post [app : Microsoft.AspNetCore.Builder.WebApplication] [pattern : String]
                    [handler : (Microsoft.AspNetCore.Http.HttpContext -> Task)]) : Unit
  (let ([erb : Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app])
    (clr-map-post erb pattern handler) ()))

(define (route/put [app : Microsoft.AspNetCore.Builder.WebApplication] [pattern : String]
                   [handler : (Microsoft.AspNetCore.Http.HttpContext -> Task)]) : Unit
  (let ([erb : Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app])
    (clr-map-put erb pattern handler) ()))

(define (route/patch [app : Microsoft.AspNetCore.Builder.WebApplication] [pattern : String]
                     [handler : (Microsoft.AspNetCore.Http.HttpContext -> Task)]) : Unit
  (let ([erb : Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app])
    (clr-map-patch erb pattern handler) ()))

(define (route/delete [app : Microsoft.AspNetCore.Builder.WebApplication] [pattern : String]
                      [handler : (Microsoft.AspNetCore.Http.HttpContext -> Task)]) : Unit
  (let ([erb : Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app])
    (clr-map-delete erb pattern handler) ()))

(export route/get route/post route/put route/patch route/delete)
