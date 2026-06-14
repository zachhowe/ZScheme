using System;
using System.IO;
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

    public static void Shutdown(WebApplication app)
    {
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.StopApplication();
    }
}

public static class RouterBridge
{
    public static void MapGet(WebApplication app, string pattern, Func<HttpContext, Task> handler) =>
        app.MapGet(pattern, handler);

    public static void MapPost(WebApplication app, string pattern, Func<HttpContext, Task> handler) =>
        app.MapPost(pattern, handler);

    public static void MapPut(WebApplication app, string pattern, Func<HttpContext, Task> handler) =>
        app.MapPut(pattern, handler);

    public static void MapPatch(WebApplication app, string pattern, Func<HttpContext, Task> handler) =>
        app.MapPatch(pattern, handler);

    public static void MapDelete(WebApplication app, string pattern, Func<HttpContext, Task> handler) =>
        app.MapDelete(pattern, handler);
}

public static class MiddlewareBridge
{
    public static void Use(WebApplication app, Func<HttpContext, Func<Task>, Task> middleware) =>
        app.Use(middleware);
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
