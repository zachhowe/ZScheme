using System.Collections;
using System.Reflection;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;

namespace ZScheme.LanguageServer;

/// <summary>
///     Forces every server capability to be advertised <em>statically</em>, in the
///     <c>initialize</c> result, rather than registered dynamically afterwards.
///     <para>
///         OmniSharp decides between the two purely from the client's
///         <c>dynamicRegistration</c> flags: when a client advertises support, the
///         capability is left out of the <c>initialize</c> result and a
///         <c>client/registerCapability</c> request is sent instead. Not every client
///         honours those. Zed, for one, advertises <c>dynamicRegistration: true</c> and
///         then logs <c>unhandled capability registration: textDocument/didOpen</c> — so
///         it never opens documents with the server and never sees a definition provider,
///         leaving every request either unanswered or resolved against a document the
///         server was never told about.
///     </para>
///     <para>
///         Clearing the flags before OmniSharp reads them makes it treat the client as
///         static-only, which every client understands. Nothing is lost: the registration
///         options this server produces are constant (a fixed document selector), so there
///         is nothing dynamic registration would buy us.
///     </para>
/// </summary>
internal static class StaticCapabilities
{
    /// <summary>Clears <c>DynamicRegistration</c> on every capability reachable from
    ///     <paramref name="capabilities" />. Returns the number of flags cleared (for
    ///     logging/tests).</summary>
    public static int ForceStatic(ClientCapabilities? capabilities)
    {
        return capabilities is null ? 0 : Visit(capabilities, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    private static int Visit(object? node, HashSet<object> seen)
    {
        if (node is null || node is string || node.GetType().IsPrimitive)
            return 0;
        if (!seen.Add(node))
            return 0;

        var cleared = 0;

        if (node is IDynamicCapability dynamic)
        {
            dynamic.DynamicRegistration = false;
            cleared++;
        }

        // Supports<T> wraps an optional capability as a struct; the payload is on Value.
        var type = node.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition().Name.StartsWith("Supports", StringComparison.Ordinal))
        {
            if (type.GetProperty("IsSupported")?.GetValue(node) is true)
                cleared += Visit(SafeGet(type.GetProperty("Value"), node), seen);
            return cleared;
        }

        if (node is IEnumerable enumerable and not IDynamicCapability)
        {
            foreach (var item in enumerable)
                cleared += Visit(item, seen);
            return cleared;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead)
                continue;
            // Only descend through the protocol's own capability graph; following
            // arbitrary BCL properties would wander off into unrelated object graphs.
            if (property.PropertyType.Namespace?.StartsWith("OmniSharp.Extensions", StringComparison.Ordinal) != true)
                continue;
            cleared += Visit(SafeGet(property, node), seen);
        }

        return cleared;
    }

    private static object? SafeGet(PropertyInfo? property, object instance)
    {
        try
        {
            return property?.GetValue(instance);
        }
        catch (TargetInvocationException)
        {
            // Optional capability accessors throw when unset; treat as absent.
            return null;
        }
    }
}
