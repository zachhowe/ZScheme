;; app.zs — WebApplication lifecycle (create-builder, build, run)
(module app)

(import-clr
  Microsoft.AspNetCore.Builder
  Microsoft.Extensions.Hosting
  System.Collections.Generic
  System.Linq

  [clr-create-builder WebApplication/CreateBuilder
    : (-> WebApplicationBuilder)]

  [clr-build WebApplicationBuilder.Build
    :instance : (WebApplicationBuilder -> WebApplication)]

  ;; Run/RunAsync/StartAsync/StopAsync take an optional url/CancellationToken the
  ;; backend supplies; only the receiver is passed here.
  [clr-run WebApplication.Run
    :instance : (WebApplication -> Unit)]
  [clr-run-async WebApplication.RunAsync
    :instance : (WebApplication -> Task)]
  [clr-start WebApplication.StartAsync
    :instance : (WebApplication -> Task)]
  [clr-stop WebApplication.StopAsync
    :instance : (WebApplication -> Task)]

  ;; Token-accepting variants. StartAsync/StopAsync have native CancellationToken
  ;; overloads. WebApplication.RunAsync has NO token overload, so running with a
  ;; token uses the IHost extension HostingAbstractionsHostExtensions.RunAsync.
  [clr-start-token WebApplication.StartAsync
    :instance : (WebApplication System.Threading.CancellationToken -> Task)]
  [clr-stop-token WebApplication.StopAsync
    :instance : (WebApplication System.Threading.CancellationToken -> Task)]
  [clr-run-async-token HostingAbstractionsHostExtensions/RunAsync
    :from "Microsoft.Extensions.Hosting.Abstractions"
    : (IHost System.Threading.CancellationToken -> Task)]

  ;; Block on a Task without an async context: Task.GetAwaiter().GetResult().
  [task-awaiter System.Threading.Tasks.Task.GetAwaiter
    :instance : (Task -> System.Runtime.CompilerServices.TaskAwaiter)]
  [awaiter-result System.Runtime.CompilerServices.TaskAwaiter.GetResult
    :instance : (System.Runtime.CompilerServices.TaskAwaiter -> Unit)]
  [disposable-dispose System.IDisposable.Dispose
    :instance : (System.IDisposable -> Unit)]

  [app-urls WebApplication.Urls
    :instance-property : (WebApplication -> (ICollection String))]
  [urls-add ICollection.Add
    :instance : ((ICollection String) String -> Unit)]
  ;; ElementAt's parameter is IEnumerable<T>; ICollection<T> is one at runtime, so
  ;; declaring it here avoids a generic-interface upcast the type checker rejects.
  [url-at Enumerable/ElementAt ^a
    : ((ICollection ^a) Int -> ^a)])

;; Create a builder with the framework's default logging providers intact. Tests
;; or quiet apps can silence logging explicitly via aspnet/logging's
;; logging/clear-providers.
(define (app/create-builder) : WebApplicationBuilder
  (clr-create-builder))

(define (app/build [builder : WebApplicationBuilder])
  : WebApplication
  (clr-build builder))

(define (app/run [app : WebApplication]) : Unit
  (clr-run app))

(define (app/run-async [app : WebApplication]) : Task
  (clr-run-async app))

;; Start Kestrel without blocking; the returned Task completes once the server is
;; bound and listening (app/first-url then holds the resolved port).
(define (app/start [app : WebApplication]) : Task
  (clr-start app))

;; Gracefully stop the host, then dispose it (releasing Kestrel, sockets, and the
;; DI container) so tests can boot a fresh host per case without accumulating them.
(define (app/shutdown [app : WebApplication]) : Unit
  (begin
    (awaiter-result (task-awaiter (clr-stop app)))
    (let ([d : System.IDisposable app])
      (disposable-dispose d))))

;; Start Kestrel without blocking, observing a caller-supplied cancellation token.
(define (app/start-with-token
          [app : WebApplication]
          [token : System.Threading.CancellationToken]) : Task
  (clr-start-token app token))

;; Run the host until the supplied token is canceled (or the host stops). Upcast
;; to IHost via a typed let, mirroring app/shutdown's IDisposable upcast, since the
;; token-aware RunAsync is an IHost extension method.
(define (app/run-async-with-token
          [app : WebApplication]
          [token : System.Threading.CancellationToken]) : Task
  (let ([h : IHost app])
    (clr-run-async-token h token)))

;; Gracefully stop the host (observing the token) then dispose it, paralleling
;; app/shutdown.
(define (app/shutdown-with-token
          [app : WebApplication]
          [token : System.Threading.CancellationToken]) : Unit
  (begin
    (awaiter-result (task-awaiter (clr-stop-token app token)))
    (let ([d : System.IDisposable app])
      (disposable-dispose d))))

(define (app/url-add [app : WebApplication] [url : String]) : Unit
  (urls-add (app-urls app) url))

;; The first configured URL. After app/start the port placeholder (":0") has been
;; replaced with the actual bound port.
(define (app/first-url [app : WebApplication]) : String
  (url-at (app-urls app) 0))

(export app/create-builder app/build app/run app/run-async
        app/start app/shutdown app/url-add app/first-url
        app/start-with-token app/run-async-with-token app/shutdown-with-token)
