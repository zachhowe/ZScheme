namespace ZScheme.Toolchain;

/// <summary>
///     Decides which toolchain a <c>zs</c> / <c>zs-lsp</c> invocation should run.
/// </summary>
/// <remarks>
///     Pure with respect to the environment: the caller passes the <c>ZSCHEME_VERSION</c> value and
///     the starting directory in, and the registry's home is injected. That keeps the whole
///     precedence chain unit-testable without setting environment variables or touching a real home.
/// </remarks>
public sealed class ToolchainResolver(ToolchainRegistry registry)
{
    /// <summary>
    ///     Resolves in precedence order: <c>ZSCHEME_VERSION</c>, then the nearest
    ///     <c>.zscheme-version</c> at or above <paramref name="startDir" />, then the global default.
    /// </summary>
    public ToolchainResolution Resolve(string? envVersion, string startDir)
    {
        var fromEnv = envVersion?.Trim();
        if (!string.IsNullOrEmpty(fromEnv))
            return Select(fromEnv, ToolchainOrigin.EnvironmentVariable, originDetail: null);

        var pin = VersionFileLocator.Find(startDir);
        if (pin is not null)
            return Select(pin.ToolchainName, ToolchainOrigin.ProjectFile, pin.FilePath);

        var fallback = registry.GetDefault();
        if (!string.IsNullOrEmpty(fallback))
            return Select(fallback, ToolchainOrigin.GlobalDefault, originDetail: null);

        return new ToolchainResolution.NoToolchains();
    }

    private ToolchainResolution Select(string name, ToolchainOrigin origin, string? originDetail)
    {
        var toolchain = registry.TryGet(name);
        if (toolchain is null)
            return new ToolchainResolution.NotInstalled(name, origin, originDetail);

        if (ToolchainRegistry.IsLinkBroken(toolchain))
            return new ToolchainResolution.LinkBroken(name, toolchain.Dir);

        return new ToolchainResolution.Resolved(toolchain, origin, originDetail);
    }
}
