using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.JsonRpc.Server;
using OmniSharp.Extensions.JsonRpc.Server.Messages;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Shared;
using OmniSharp.Extensions.LanguageServer.Server;
using Serilog;

namespace ZScheme.LanguageServer;

/// <summary>
///     Replaces OmniSharp's <see cref="LspServerReceiver" /> so a notification that arrives
///     before the initialize handshake finishes is <b>set aside and replayed</b> rather than
///     discarded.
/// </summary>
/// <remarks>
///     <para>
///         The receiver's gate is evaluated on the thread reading the input pipe, which runs
///         ahead of the handling of the messages it has already read. A client that pipelines
///         its startup — writing initialize, initialized and didOpen back to back instead of
///         waiting for the initialize response, which real editors do — therefore has its
///         didOpen examined while the initialize request is still queued. Upstream drops it
///         with a warning, and because didOpen is a notification there is no response for the
///         client to notice missing: the document is never analysed, no diagnostics are ever
///         published, and every navigation request answers "no result" forever.
///     </para>
///     <para>
///         Waiting for the handshake in place is not an option: the messages the reader has
///         already handed on are dispatched on the reader's own thread (<c>InputHandler</c>
///         subscribes its input queue with no scheduler), so blocking the reader starves the
///         very initialize request it would be waiting for. Letting the notification through
///         immediately is no better, because the handler it routes to is not registered until
///         initialize has been handled. So the notification is set aside and replayed
///         afterwards, through <see cref="ReplayHeldNotificationsAsync" />, once handlers
///         exist.
///     </para>
/// </remarks>
internal sealed class HandshakeAwareReceiver : LspServerReceiver, IReceiver
{
    private static readonly ILogger Log = Serilog.Log.ForContext<HandshakeAwareReceiver>();

    /// <summary>Bound on how many notifications are kept, so a client that never finishes the
    ///     handshake cannot grow this without limit. Far above the handful a real startup
    ///     pipelines.</summary>
    private const int HoldLimit = 512;

    private readonly List<Notification> _held = [];
    private readonly object _lock = new();
    private bool _handshakeComplete;

    /// <summary>Lets the post-handshake path skip the lock on every message, which is the
    ///     whole steady-state traffic of the server.</summary>
    private volatile bool _holdingAny;

    public HandshakeAwareReceiver()
        // Base logging only fires from the base GetRequests, which the override reaches only
        // on the initialized fast path; this class logs its own decisions through Serilog.
        : base(NullLogger<LspServerReceiver>.Instance) { }

    /// <summary>Closes the holding window. <see cref="Receiver.Initialized" /> is sealed
    ///     against overriding, so <see cref="IReceiver" /> is re-implemented to intercept the
    ///     call the server makes through the interface.</summary>
    void IReceiver.Initialized()
    {
        Initialized();
        lock (_lock)
        {
            _handshakeComplete = true;
        }
    }

    public override (IEnumerable<Renor> results, bool hasResponse) GetRequests(JToken container)
    {
        // Once initialized the base implementation is a straight pass-through to Receiver.
        if (_initialized)
        {
            var (routed, hadResponse) = base.GetRequests(container);
            if (!_holdingAny)
                return (routed, hadResponse);

            // Handlers exist now, so anything still held can ride out on the normal path —
            // ahead of this message, which arrived after it.
            List<Renor> withHeldFirst = [.. TakeHeld().Select(held => (Renor)held), .. routed];
            return (withHeldFirst, hadResponse);
        }

        var parsed = container is JArray batch
            ? batch.Select(GetRenor).ToList()
            : [GetRenor(container)];

        return (Gate(parsed), parsed.Any(item => item.IsResponse));
    }

    /// <summary>
    ///     Routes everything held during the handshake. Called once the server is running, so
    ///     the handlers these notifications need are registered by now.
    /// </summary>
    public async Task ReplayHeldNotificationsAsync(IRequestRouter<ILspHandlerDescriptor> router)
    {
        foreach (var notification in TakeHeld())
        {
            Log.Information(
                "Replaying {Method}, which arrived before the handshake completed",
                notification.Method
            );
            try
            {
                var descriptors = router.GetDescriptors(notification);
                if (!descriptors.Any())
                {
                    Log.Warning("Dropping held {Method}: no handler", notification.Method);
                    continue;
                }

                await router.RouteNotification(descriptors, notification, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Best effort: a replay is recovery from a client that broke the handshake
                // ordering, and must not take the server down with it.
                Log.Error(ex, "Replay of held {Method} failed", notification.Method);
            }
        }
    }

    /// <summary>Decides each message's fate now rather than on enumeration, so holding does
    ///     not depend on the caller draining the result.</summary>
    private List<Renor> Gate(List<Renor> parsed)
    {
        var routable = new List<Renor>();
        foreach (var item in parsed)
        {
            switch (item)
            {
                case { IsResponse: true }:
                case { IsRequest: true, Request.Method: GeneralNames.Initialize }:
                case { IsNotification: true, Notification.Method: GeneralNames.Initialized }:
                    routable.Add(item);
                    break;

                case { IsRequest: true, Request: { } request }:
                    // A request can report the problem to the client, so say so rather than
                    // holding it. Spelled out because upstream's ServerNotInitialized has an
                    // internal constructor; this is the error it builds.
                    Log.Warning(
                        "Refusing request {Method} received before initialization completed",
                        request.Method
                    );
                    routable.Add(
                        new RpcError(
                            null,
                            request.Method,
                            new ErrorMessage(-32002, "Server Not Initialized")
                        )
                    );
                    break;

                case { IsNotification: true, Notification: { } notification }:
                    // Withheld, unless the handshake completed while this batch was being
                    // gated — in which case handlers exist and it can go straight through.
                    if (!ShouldWithhold(notification))
                        routable.Add(item);
                    break;

                case { IsError: true, Error: { } error }:
                    Log.Warning(
                        "Discarding error {Method} received before initialization completed",
                        error.Method
                    );
                    break;

                default:
                    Log.Error("Discarding unrecognized message {@Message}", item);
                    break;
            }
        }

        return routable;
    }

    /// <summary>True when <paramref name="notification" /> must not be routed now — either
    ///     because it was kept for replay, or because too many already have been.</summary>
    private bool ShouldWithhold(Notification notification)
    {
        lock (_lock)
        {
            if (_handshakeComplete)
                return false;

            if (_held.Count >= HoldLimit)
            {
                Log.Warning(
                    "Dropping notification {Method}: {Limit} already held for replay",
                    notification.Method,
                    HoldLimit
                );
                return true;
            }

            _held.Add(notification);
            _holdingAny = true;
            return true;
        }
    }

    private List<Notification> TakeHeld()
    {
        lock (_lock)
        {
            List<Notification> held = [.. _held];
            _held.Clear();
            _holdingAny = false;
            return held;
        }
    }
}
