using System.Collections;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.JsonRpc.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Shared;
using Xunit;

namespace ZScheme.LanguageServer.Tests;

/// <summary>
///     Unit coverage for the receiver that keeps a pipelining client's notifications alive
///     across the initialize handshake. <c>StdioServerTests</c> covers the same ground against
///     a real process, but only if the write race actually lands; these pin the decisions
///     directly.
/// </summary>
public sealed class HandshakeAwareReceiverTests
{
    private static JObject DidOpen(string uri) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "textDocument/didOpen",
            ["params"] = new JObject
            {
                ["textDocument"] = new JObject
                {
                    ["uri"] = uri,
                    ["languageId"] = "zscheme",
                    ["version"] = 1,
                    ["text"] = "(module m)",
                },
            },
        };

    [Fact]
    public void NotificationBeforeHandshakeCompletes_IsHeldRatherThanRouted()
    {
        var receiver = new HandshakeAwareReceiver();

        var (results, _) = receiver.GetRequests(DidOpen("file:///a.zs"));

        Assert.Empty(results);
    }

    [Fact]
    public async Task HeldNotification_IsReplayed_OnceHandlersExist()
    {
        var receiver = new HandshakeAwareReceiver();
        var router = new RecordingRouter();

        var (results, _) = receiver.GetRequests(DidOpen("file:///a.zs"));
        Assert.Empty(results);
        ((IReceiver)receiver).Initialized();

        await receiver.ReplayHeldNotificationsAsync(router);

        var routed = Assert.Single(router.RoutedNotifications);
        Assert.Equal("textDocument/didOpen", routed.Method);
        Assert.Equal("file:///a.zs", (string?)routed.Params?["textDocument"]?["uri"]);
    }

    [Fact]
    public async Task HeldNotifications_AreReplayedInArrivalOrder()
    {
        var receiver = new HandshakeAwareReceiver();
        var router = new RecordingRouter();

        receiver.GetRequests(DidOpen("file:///first.zs"));
        receiver.GetRequests(DidOpen("file:///second.zs"));
        ((IReceiver)receiver).Initialized();

        await receiver.ReplayHeldNotificationsAsync(router);

        Assert.Equal(
            ["file:///first.zs", "file:///second.zs"],
            router.RoutedNotifications.Select(n => (string?)n.Params?["textDocument"]?["uri"])
        );
    }

    [Fact]
    public async Task ReplayDeliversEachNotificationOnce()
    {
        var receiver = new HandshakeAwareReceiver();
        var router = new RecordingRouter();

        receiver.GetRequests(DidOpen("file:///a.zs"));
        ((IReceiver)receiver).Initialized();

        await receiver.ReplayHeldNotificationsAsync(router);
        await receiver.ReplayHeldNotificationsAsync(router);

        Assert.Single(router.RoutedNotifications);
    }

    /// <summary>If the client keeps talking once the handshake lands, the held notification
    ///     rides out on the normal path — ahead of the message that arrived after it, so a
    ///     didOpen can never be overtaken by the didChange that followed it.</summary>
    [Fact]
    public void HeldNotification_LeadsTheNextBatch_WithoutWaitingForReplay()
    {
        var receiver = new HandshakeAwareReceiver();

        receiver.GetRequests(DidOpen("file:///held.zs"));
        ((IReceiver)receiver).Initialized();
        var (results, _) = receiver.GetRequests(DidOpen("file:///later.zs"));

        Assert.Equal(
            ["file:///held.zs", "file:///later.zs"],
            results.Select(r => (string?)r.Notification!.Params?["textDocument"]?["uri"])
        );
    }

    [Fact]
    public void NotificationAfterHandshakeCompletes_IsRoutedImmediately()
    {
        var receiver = new HandshakeAwareReceiver();
        ((IReceiver)receiver).Initialized();

        var (results, _) = receiver.GetRequests(DidOpen("file:///a.zs"));

        var item = Assert.Single(results);
        Assert.True(item.IsNotification);
        Assert.Equal("textDocument/didOpen", item.Notification!.Method);
    }

    [Fact]
    public void InitializeAndInitialized_AreNeverHeld()
    {
        var receiver = new HandshakeAwareReceiver();

        var (initialize, _) = receiver.GetRequests(
            JObject.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""")
        );
        var (initialized, _) = receiver.GetRequests(
            JObject.Parse("""{"jsonrpc":"2.0","method":"initialized","params":{}}""")
        );

        Assert.True(Assert.Single(initialize).IsRequest);
        Assert.True(Assert.Single(initialized).IsNotification);
    }

    /// <summary>Unlike a notification, a request can be told the server is not ready — so it
    ///     is answered rather than held, which is what upstream does too.</summary>
    [Fact]
    public void RequestBeforeHandshakeCompletes_IsRefusedWithServerNotInitialized()
    {
        var receiver = new HandshakeAwareReceiver();

        var (results, _) = receiver.GetRequests(
            JObject.Parse(
                """{"jsonrpc":"2.0","id":7,"method":"textDocument/definition","params":{}}"""
            )
        );

        var item = Assert.Single(results);
        Assert.True(item.IsError);
        Assert.Equal(-32002, item.Error!.Error!.Code);
    }

    [Fact]
    public async Task HoldingIsBounded_SoAnUnfinishedHandshakeCannotGrowWithoutLimit()
    {
        var receiver = new HandshakeAwareReceiver();
        var router = new RecordingRouter();

        // One past the 512 cap; the extra is dropped (with a warning) rather than retained.
        for (var i = 0; i < 513; i++)
            receiver.GetRequests(DidOpen($"file:///f{i}.zs"));
        ((IReceiver)receiver).Initialized();

        await receiver.ReplayHeldNotificationsAsync(router);

        Assert.Equal(512, router.RoutedNotifications.Count);
    }

    /// <summary>Records what was routed. No logic — see docs/MOCKS.md.</summary>
    private sealed class RecordingRouter : IRequestRouter<ILspHandlerDescriptor>
    {
        public List<Notification> RoutedNotifications { get; } = [];

        public IRequestDescriptor<ILspHandlerDescriptor> GetDescriptors(Notification notification)
        {
            return new OneDescriptor();
        }

        public IRequestDescriptor<ILspHandlerDescriptor> GetDescriptors(Request request)
        {
            return new OneDescriptor();
        }

        public Task RouteNotification(
            IRequestDescriptor<ILspHandlerDescriptor> descriptors,
            Notification notification,
            CancellationToken token
        )
        {
            RoutedNotifications.Add(notification);
            return Task.CompletedTask;
        }

        public Task<ErrorResponse> RouteRequest(
            IRequestDescriptor<ILspHandlerDescriptor> descriptors,
            Request request,
            CancellationToken token
        )
        {
            throw new NotSupportedException("requests are never held");
        }

        /// <summary>A non-empty descriptor set. The receiver only asks whether one exists, so
        ///     the element itself is never dereferenced.</summary>
        private sealed class OneDescriptor : IRequestDescriptor<ILspHandlerDescriptor>
        {
            public ILspHandlerDescriptor Default => null!;

            public IEnumerator<ILspHandlerDescriptor> GetEnumerator()
            {
                yield return null!;
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
