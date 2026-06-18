using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ZScheme.AspNet.Bridge;

/// <summary>
///     Wrappers around ASP.NET Core APIs whose signatures are ergonomic for
///     ZScheme's <c>import-clr</c> binder. Each method has unambiguous overloads
///     and accepts <see cref="Func{HttpContext, Task}"/> directly so ZScheme
///     handler functions can be passed without delegate-type conversions.
/// </summary>
public static class WebAppBridge
{
    public static WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        return builder;
    }

    public static WebApplication BuildApp(WebApplicationBuilder builder) =>
        builder.Build();

    public static void Run(WebApplication app) => app.Run();

    public static Task RunAsync(WebApplication app) => app.RunAsync();

    public static void AddUrl(WebApplication app, string url) =>
        app.Urls.Add(url);

    public static string GetFirstUrl(WebApplication app)
    {
        // Kestrel updates app.Urls with the actual bound port asynchronously
        // after RunAsync() starts. Wait for the port to be resolved (no longer ":0")
        // so callers don't get an unusable URL.
        var maxWait = TimeSpan.FromMilliseconds(2000);
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < maxWait)
        {
            foreach (var url in app.Urls)
            {
                if (!string.IsNullOrEmpty(url) && !url.Contains(":0/") && !url.EndsWith(":0"))
                    return url;
            }
            System.Threading.Thread.Sleep(10);
        }
        // Fallback: return whatever is configured (may still be :0)
        foreach (var url in app.Urls) return url;
        return string.Empty;
    }

    public static async Task RunInBackground(WebApplication app)
    {
        await Task.Run(() => app.RunAsync());
    }

    public static async Task RunInBackgroundWithWait(WebApplication app)
    {
        var tcs = new TaskCompletionSource();
        var task = app.RunAsync();
        #pragma warning disable CS4014
       task.ContinueWith(_ => tcs.SetException(task.Exception?.InnerException ?? task.Exception ?? new Exception("RunAsync failed")), TaskContinuationOptions.OnlyOnFaulted);
       #pragma warning restore CS4014

        // Wait for the port to be resolved (no longer ":0")
        var maxWait = TimeSpan.FromMilliseconds(5000);
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < maxWait)
        {
            foreach (var url in app.Urls)
            {
                if (!string.IsNullOrEmpty(url) && !url.Contains(":0/") && !url.EndsWith(":0"))
                {
                    // Give Kestrel a moment to actually be listening
                    await Task.Delay(100);
                    return;
                }
            }
            await Task.Delay(10);
        }
        // Fallback: just return, the caller will poll
    }

    // Starts the host and returns once Kestrel is bound and listening.
    // app.Urls holds the resolved port (no longer ":0") when this completes.
    public static Task StartServer(WebApplication app) => app.StartAsync();

    // Stops and fully disposes the host so its DI container, Kestrel transport,
    // sockets, and worker threads are released. Tests boot a fresh host per case;
    // without disposal these accumulate and eventually crash the process.
    public static void Shutdown(WebApplication app)
    {
        app.StopAsync().GetAwaiter().GetResult();
        ((IAsyncDisposable)app).DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

public static class RouterBridge
{
    // The handler must be registered through the RequestDelegate overload, NOT the
    // Minimal-API Delegate overload. A Func<HttpContext, Task> is not implicitly
    // convertible to RequestDelegate, so `app.MapGet(pattern, handler)` would bind
    // to MapGet(string, Delegate) and run RequestDelegateFactory over the handler —
    // which treats the handler's parameter as a JSON body argument. A request with
    // Content-Type: application/json and a valid JSON body then gets deserialized
    // and passed to the handler in place of the HttpContext, causing a native crash.
    // Coercing through a RequestDelegate local selects the raw overload and hands the
    // handler the real HttpContext.
    public static void MapGet(WebApplication app, string pattern, Func<HttpContext, Task> handler)
    {
        RequestDelegate rd = ctx => handler(ctx);
        app.MapGet(pattern, rd);
    }

    public static void MapPost(WebApplication app, string pattern, Func<HttpContext, Task> handler)
    {
        RequestDelegate rd = ctx => handler(ctx);
        app.MapPost(pattern, rd);
    }

    public static void MapPut(WebApplication app, string pattern, Func<HttpContext, Task> handler)
    {
        RequestDelegate rd = ctx => handler(ctx);
        app.MapPut(pattern, rd);
    }

    public static void MapPatch(WebApplication app, string pattern, Func<HttpContext, Task> handler)
    {
        RequestDelegate rd = ctx => handler(ctx);
        app.MapPatch(pattern, rd);
    }

    public static void MapDelete(WebApplication app, string pattern, Func<HttpContext, Task> handler)
    {
        RequestDelegate rd = ctx => handler(ctx);
        app.MapDelete(pattern, rd);
    }
}

public static class MiddlewareBridge
{
    public static void Use(WebApplication app, Func<HttpContext, Func<Task>, Task> middleware) =>
        app.Use(middleware);
}

public static class AuthBridge
{
    // Validate an HTTP Basic Authorization header value against expected credentials.
    // Returns true only for a well-formed `Basic <base64(user:pass)>` whose decoded
    // username and password match. Any malformed/missing header returns false.
    public static bool CheckBasic(string authHeader, string user, string pass)
    {
        const string scheme = "Basic ";
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith(scheme, StringComparison.Ordinal))
            return false;

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authHeader[scheme.Length..]));
        }
        catch (FormatException)
        {
            return false;
        }

        var sep = decoded.IndexOf(':');
        if (sep < 0)
            return false;

        return decoded[..sep] == user && decoded[(sep + 1)..] == pass;
    }
}

public static class RequestBridge
{
    public static string GetMethod(HttpContext ctx) => ctx.Request.Method;

    public static string GetPath(HttpContext ctx) => ctx.Request.Path.Value ?? "";

    public static string GetRouteValue(HttpContext ctx, string key, string fallback) =>
        ctx.Request.RouteValues.TryGetValue(key, out var v) ? v?.ToString() ?? fallback : fallback;

    public static string GetQuery(HttpContext ctx, string key, string fallback) =>
        ctx.Request.Query.TryGetValue(key, out var v) ? v.ToString() : fallback;

    public static string GetHeader(HttpContext ctx, string key, string fallback) =>
        ctx.Request.Headers.TryGetValue(key, out var v) ? v.ToString() : fallback;

    public static async Task<string> ReadBodyString(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        return await reader.ReadToEndAsync();
    }
}

public static class ResponseBridge
{
    public static void SetStatus(HttpContext ctx, int code) => ctx.Response.StatusCode = code;

    public static void SetHeader(HttpContext ctx, string name, string value) =>
        ctx.Response.Headers.Append(name, value);

    public static Task WriteString(HttpContext ctx, string body) =>
        ctx.Response.WriteAsync(body);

    public static Task WriteJson(HttpContext ctx, string json)
    {
        ctx.Response.Headers.ContentType = "application/json; charset=utf-8";
        return ctx.Response.WriteAsync(json);
    }
}
