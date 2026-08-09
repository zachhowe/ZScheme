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

  [clr-map-get EndpointRouteBuilderExtensions/MapGet
    :from "Microsoft.AspNetCore.Routing"
    : (IEndpointRouteBuilder String
       (delegate Microsoft.AspNetCore.Http.RequestDelegate)
       -> IEndpointConventionBuilder)]

  [clr-map-post EndpointRouteBuilderExtensions/MapPost
    :from "Microsoft.AspNetCore.Routing"
    : (IEndpointRouteBuilder String
       (delegate Microsoft.AspNetCore.Http.RequestDelegate)
       -> IEndpointConventionBuilder)]

  [clr-map-put EndpointRouteBuilderExtensions/MapPut
    :from "Microsoft.AspNetCore.Routing"
    : (IEndpointRouteBuilder String
       (delegate Microsoft.AspNetCore.Http.RequestDelegate)
       -> IEndpointConventionBuilder)]

  [clr-map-patch EndpointRouteBuilderExtensions/MapPatch
    :from "Microsoft.AspNetCore.Routing"
    : (IEndpointRouteBuilder String
       (delegate Microsoft.AspNetCore.Http.RequestDelegate)
       -> IEndpointConventionBuilder)]

  [clr-map-delete EndpointRouteBuilderExtensions/MapDelete
    :from "Microsoft.AspNetCore.Routing"
    : (IEndpointRouteBuilder String
       (delegate Microsoft.AspNetCore.Http.RequestDelegate)
       -> IEndpointConventionBuilder)])

;; WebApplication implements IEndpointRouteBuilder; upcast then register, discarding
;; the returned IEndpointConventionBuilder so the public surface stays `-> Unit`.
(define (route/get [app : WebApplication] [pattern : String]
                   [handler : (HttpContext -> Task)]) : Unit
  (let ([erb : IEndpointRouteBuilder app])
    (clr-map-get erb pattern handler) ()))

(define (route/post [app : WebApplication] [pattern : String]
                    [handler : (HttpContext -> Task)]) : Unit
  (let ([erb : IEndpointRouteBuilder app])
    (clr-map-post erb pattern handler) ()))

(define (route/put [app : WebApplication] [pattern : String]
                   [handler : (HttpContext -> Task)]) : Unit
  (let ([erb : IEndpointRouteBuilder app])
    (clr-map-put erb pattern handler) ()))

(define (route/patch [app : WebApplication] [pattern : String]
                     [handler : (HttpContext -> Task)]) : Unit
  (let ([erb : IEndpointRouteBuilder app])
    (clr-map-patch erb pattern handler) ()))

(define (route/delete [app : WebApplication] [pattern : String]
                      [handler : (HttpContext -> Task)]) : Unit
  (let ([erb : IEndpointRouteBuilder app])
    (clr-map-delete erb pattern handler) ()))

(export route/get route/post route/put route/patch route/delete)
