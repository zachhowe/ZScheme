;; app.zs — WebApplication lifecycle (create-builder, build, run)
(module app)

(import-clr
  Microsoft.AspNetCore.Builder
  Microsoft.Extensions.Hosting
  System.Collections.Generic
  System.Linq

  [clr-create-builder Microsoft.AspNetCore.Builder.WebApplication/CreateBuilder
    : (-> Microsoft.AspNetCore.Builder.WebApplicationBuilder)]

  [clr-build Microsoft.AspNetCore.Builder.WebApplicationBuilder.Build
    :instance : (Microsoft.AspNetCore.Builder.WebApplicationBuilder
                 -> Microsoft.AspNetCore.Builder.WebApplication)]

  ;; Run/RunAsync/StartAsync/StopAsync take an optional url/CancellationToken the
  ;; backend supplies; only the receiver is passed here.
  [clr-run Microsoft.AspNetCore.Builder.WebApplication.Run
    :instance : (Microsoft.AspNetCore.Builder.WebApplication -> Unit)]
  [clr-run-async Microsoft.AspNetCore.Builder.WebApplication.RunAsync
    :instance : (Microsoft.AspNetCore.Builder.WebApplication -> Task)]
  [clr-start Microsoft.AspNetCore.Builder.WebApplication.StartAsync
    :instance : (Microsoft.AspNetCore.Builder.WebApplication -> Task)]
  [clr-stop Microsoft.AspNetCore.Builder.WebApplication.StopAsync
    :instance : (Microsoft.AspNetCore.Builder.WebApplication -> Task)]

  ;; Token-accepting variants. StartAsync/StopAsync have native CancellationToken
  ;; overloads. WebApplication.RunAsync has NO token overload, so running with a
  ;; token uses the IHost extension HostingAbstractionsHostExtensions.RunAsync.
  [clr-start-token Microsoft.AspNetCore.Builder.WebApplication.StartAsync
    :instance : (Microsoft.AspNetCore.Builder.WebApplication
                 System.Threading.CancellationToken -> Task)]
  [clr-stop-token Microsoft.AspNetCore.Builder.WebApplication.StopAsync
    :instance : (Microsoft.AspNetCore.Builder.WebApplication
                 System.Threading.CancellationToken -> Task)]
  [clr-run-async-token Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions/RunAsync
    :from "Microsoft.Extensions.Hosting.Abstractions"
    : (Microsoft.Extensions.Hosting.IHost
       System.Threading.CancellationToken -> Task)]

  ;; Block on a Task without an async context: Task.GetAwaiter().GetResult().
  [task-awaiter System.Threading.Tasks.Task.GetAwaiter
    :instance : (Task -> System.Runtime.CompilerServices.TaskAwaiter)]
  [awaiter-result System.Runtime.CompilerServices.TaskAwaiter.GetResult
    :instance : (System.Runtime.CompilerServices.TaskAwaiter -> Unit)]
  [disposable-dispose System.IDisposable.Dispose
    :instance : (System.IDisposable -> Unit)]

  [app-urls Microsoft.AspNetCore.Builder.WebApplication.Urls
    :instance-property : (Microsoft.AspNetCore.Builder.WebApplication
                          -> (System.Collections.Generic.ICollection String))]
  [urls-add System.Collections.Generic.ICollection.Add
    :instance : ((System.Collections.Generic.ICollection String) String -> Unit)]
  ;; ElementAt's parameter is IEnumerable<T>; ICollection<T> is one at runtime, so
  ;; declaring it here avoids a generic-interface upcast the type checker rejects.
  [url-at System.Linq.Enumerable/ElementAt ^a
    : ((System.Collections.Generic.ICollection ^a) Int -> ^a)])

;; Create a builder with the framework's default logging providers intact. Tests
;; or quiet apps can silence logging explicitly via aspnet/logging's
;; logging/clear-providers.
(define (app/create-builder) : Microsoft.AspNetCore.Builder.WebApplicationBuilder
  (clr-create-builder))

(define (app/build [builder : Microsoft.AspNetCore.Builder.WebApplicationBuilder])
  : Microsoft.AspNetCore.Builder.WebApplication
  (clr-build builder))

(define (app/run [app : Microsoft.AspNetCore.Builder.WebApplication]) : Unit
  (clr-run app))

(define (app/run-async [app : Microsoft.AspNetCore.Builder.WebApplication]) : Task
  (clr-run-async app))

;; Start Kestrel without blocking; the returned Task completes once the server is
;; bound and listening (app/first-url then holds the resolved port).
(define (app/start [app : Microsoft.AspNetCore.Builder.WebApplication]) : Task
  (clr-start app))

;; Gracefully stop the host, then dispose it (releasing Kestrel, sockets, and the
;; DI container) so tests can boot a fresh host per case without accumulating them.
(define (app/shutdown [app : Microsoft.AspNetCore.Builder.WebApplication]) : Unit
  (begin
    (awaiter-result (task-awaiter (clr-stop app)))
    (let ([d : System.IDisposable app])
      (disposable-dispose d))))

;; Start Kestrel without blocking, observing a caller-supplied cancellation token.
(define (app/start-with-token
          [app : Microsoft.AspNetCore.Builder.WebApplication]
          [token : System.Threading.CancellationToken]) : Task
  (clr-start-token app token))

;; Run the host until the supplied token is canceled (or the host stops). Upcast
;; to IHost via a typed let, mirroring app/shutdown's IDisposable upcast, since the
;; token-aware RunAsync is an IHost extension method.
(define (app/run-async-with-token
          [app : Microsoft.AspNetCore.Builder.WebApplication]
          [token : System.Threading.CancellationToken]) : Task
  (let ([h : Microsoft.Extensions.Hosting.IHost app])
    (clr-run-async-token h token)))

;; Gracefully stop the host (observing the token) then dispose it, paralleling
;; app/shutdown.
(define (app/shutdown-with-token
          [app : Microsoft.AspNetCore.Builder.WebApplication]
          [token : System.Threading.CancellationToken]) : Unit
  (begin
    (awaiter-result (task-awaiter (clr-stop-token app token)))
    (let ([d : System.IDisposable app])
      (disposable-dispose d))))

(define (app/url-add [app : Microsoft.AspNetCore.Builder.WebApplication] [url : String]) : Unit
  (urls-add (app-urls app) url))

;; The first configured URL. After app/start the port placeholder (":0") has been
;; replaced with the actual bound port.
(define (app/first-url [app : Microsoft.AspNetCore.Builder.WebApplication]) : String
  (url-at (app-urls app) 0))

(export app/create-builder app/build app/run app/run-async
        app/start app/shutdown app/url-add app/first-url
        app/start-with-token app/run-async-with-token app/shutdown-with-token)
