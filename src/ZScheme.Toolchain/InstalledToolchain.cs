namespace ZScheme.Toolchain;

/// <summary>A toolchain present under <c>~/.zscheme/toolchains/</c>.</summary>
/// <param name="Name">The selectable name, e.g. <c>0.4.0</c> or <c>dev</c>.</param>
/// <param name="Dir">Root directory of the toolchain (the link target, for a linked toolchain).</param>
/// <param name="BinDir">Directory holding <c>zs</c> and <c>zs-lsp</c>.</param>
/// <param name="IsLinked">True when registered by <c>zsup link</c> rather than installed.</param>
/// <param name="LinkTargetPath">The path recorded in the <c>.link</c> file, when linked.</param>
public sealed record InstalledToolchain(
    string Name,
    string Dir,
    string BinDir,
    bool IsLinked,
    string? LinkTargetPath
)
{
    /// <summary>Absolute path of one of the toolchain's executables.</summary>
    public string GetExecutablePath(string baseName)
    {
        return Path.Combine(BinDir, ZSchemeHome.ExeName(baseName));
    }
}
