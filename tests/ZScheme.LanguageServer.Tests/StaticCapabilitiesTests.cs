using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using Xunit;
using ZScheme.LanguageServer;

namespace ZScheme.LanguageServer.Tests;

/// <summary>
///     Regression tests for the capability-advertisement bug: the server used to defer
///     every capability to <c>client/registerCapability</c> whenever the client claimed
///     <c>dynamicRegistration</c> support. Zed makes that claim and then ignores the
///     registrations, so it never sent <c>textDocument/didOpen</c> and saw no definition
///     provider — every navigation request silently resolved to nothing.
/// </summary>
public sealed class StaticCapabilitiesTests
{
    [Fact]
    public void ForceStatic_ClearsDynamicRegistration_OnNavigationCapabilities()
    {
        var capabilities = new ClientCapabilities
        {
            TextDocument = new TextDocumentClientCapabilities
            {
                Synchronization = new TextSynchronizationCapability { DynamicRegistration = true },
                Definition = new DefinitionCapability { DynamicRegistration = true },
                Declaration = new DeclarationCapability { DynamicRegistration = true },
                Hover = new HoverCapability { DynamicRegistration = true },
                References = new ReferenceCapability { DynamicRegistration = true },
            },
        };

        var cleared = StaticCapabilities.ForceStatic(capabilities);

        Assert.True(cleared >= 5);
        Assert.False(capabilities.TextDocument!.Synchronization.Value!.DynamicRegistration);
        Assert.False(capabilities.TextDocument.Definition.Value!.DynamicRegistration);
        Assert.False(capabilities.TextDocument.Declaration.Value!.DynamicRegistration);
        Assert.False(capabilities.TextDocument.Hover.Value!.DynamicRegistration);
        Assert.False(capabilities.TextDocument.References.Value!.DynamicRegistration);
    }

    [Fact]
    public void ForceStatic_ClearsWorkspaceCapabilities()
    {
        var capabilities = new ClientCapabilities
        {
            Workspace = new WorkspaceClientCapabilities
            {
                DidChangeWatchedFiles = new DidChangeWatchedFilesCapability
                {
                    DynamicRegistration = true,
                },
                Symbol = new WorkspaceSymbolCapability { DynamicRegistration = true },
            },
        };

        var cleared = StaticCapabilities.ForceStatic(capabilities);

        Assert.True(cleared >= 2);
        Assert.False(capabilities.Workspace!.DidChangeWatchedFiles.Value!.DynamicRegistration);
        Assert.False(capabilities.Workspace.Symbol.Value!.DynamicRegistration);
    }

    [Fact]
    public void ForceStatic_ToleratesNullAndUnsetCapabilities()
    {
        Assert.Equal(0, StaticCapabilities.ForceStatic(null));
        // An entirely empty capability set has nothing to clear and must not throw on the
        // unset Supports<T> accessors.
        Assert.Equal(0, StaticCapabilities.ForceStatic(new ClientCapabilities()));
    }
}
