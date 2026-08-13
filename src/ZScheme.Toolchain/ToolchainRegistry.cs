using System.Text.Json;

namespace ZScheme.Toolchain;

/// <summary>
///     A filesystem view of <c>~/.zscheme</c>: which toolchains exist, which one is the default, and
///     the mutations behind <c>zsup use</c>, <c>zsup link</c>, and <c>zsup uninstall</c>.
/// </summary>
/// <remarks>
///     The home is injected, and nothing here reads the environment, so tests operate entirely
///     inside a temporary directory.
/// </remarks>
public sealed class ToolchainRegistry(string home)
{
    /// <summary>
    ///     Thrown when a toolchain was removed but the settings file that still records it as the
    ///     default could not be rewritten.
    /// </summary>
    /// <remarks>
    ///     Its own type because the two halves of <see cref="Remove" /> and <see cref="Unlink" />
    ///     fail for the same reasons and mean opposite things: a read-only home or a full disk stops
    ///     the delete, and the toolchain is still there, or it stops the settings write afterwards,
    ///     and the toolchain is gone. Reporting the second as
    ///     <c>could not remove toolchain 'x'</c> would tell the user to retry something that has
    ///     already happened. Derived from <see cref="IOException" /> so a caller that has not been
    ///     taught the difference still catches it where it catches every other write failure.
    /// </remarks>
    public sealed class DefaultNotClearedException(string name, Exception inner)
        : IOException(
            $"removed '{name}', but it is still recorded as the default toolchain: {inner.Message}",
            inner
        )
    {
        /// <summary>The toolchain that was removed and is still named as the default.</summary>
        public string ToolchainName { get; } = name;
    }

    /// <summary>The resolved home this registry operates on.</summary>
    public string Home { get; } = ZSchemeHome.GetHome(home);

    /// <summary>
    ///     Every installed and linked toolchain, ordered by name. Broken links are included — a
    ///     link whose target has been deleted still needs to be listed so it can be repaired.
    /// </summary>
    public IReadOnlyList<InstalledToolchain> List()
    {
        var toolchainsDir = ZSchemeHome.GetToolchainsDir(Home);
        if (!Directory.Exists(toolchainsDir))
            return [];

        var found = new List<InstalledToolchain>();
        var seen = new HashSet<string>(ToolchainName.Comparer);

        foreach (var dir in Directory.EnumerateDirectories(toolchainsDir))
        {
            var name = Path.GetFileName(dir);
            // Skips .staging-*/.trash-* as well as anything else unselectable.
            if (ToolchainName.IsValid(name) && seen.Add(name))
                found.Add(Installed(name, dir));
        }

        foreach (
            var file in Directory.EnumerateFiles(toolchainsDir, "*" + ZSchemeHome.LinkFileExtension)
        )
        {
            var name = Path.GetFileNameWithoutExtension(file);
            // A directory of the same name wins, matching TryGet. The installer refuses to create
            // that collision, but a home predating it -- or one edited by hand -- can still have
            // one, and listing the name twice would be worse than shadowing the link.
            if (!ToolchainName.IsValid(name) || !seen.Add(name))
                continue;

            var target = ReadLinkTarget(file);
            if (target is not null)
                found.Add(Linked(name, target));
        }

        return [.. found.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    ///     Installed toolchains whose payload reports <paramref name="compilerVersion" /> — that is,
    ///     everything sharing the <c>cache/pkg/&lt;version&gt;</c> that version keys.
    /// </summary>
    /// <remarks>
    ///     More than one is ordinary: <c>zsup install dev --from &lt;0.4.0 archive&gt;</c> beside
    ///     <c>zsup install 0.4.0</c> gives two toolchains built from one payload. Linked toolchains
    ///     are excluded because they get an isolated <see cref="ZSchemeHome.GetLinkedCacheRoot" />
    ///     instead of sharing this one.
    /// </remarks>
    public IReadOnlyList<InstalledToolchain> UsingCompilerVersion(string compilerVersion)
    {
        return
        [
            .. List()
                .Where(t =>
                    !t.IsLinked
                    && ToolchainName.AreSame(
                        PackageCacheSeeder.ResolveCompilerVersion(t.Dir, t.Name),
                        compilerVersion
                    )
                ),
        ];
    }

    /// <summary>
    ///     Looks up a single toolchain by name, or <c>null</c> if neither a directory nor a
    ///     <c>.link</c> file exists for it. A link pointing at a missing directory is still
    ///     returned; callers distinguish that case by checking <see cref="IsLinkBroken" />.
    /// </summary>
    public InstalledToolchain? TryGet(string name)
    {
        if (!ToolchainName.IsValid(name))
            return null;

        var dir = ZSchemeHome.GetToolchainDir(name, Home);
        if (Directory.Exists(dir))
            return Installed(name, dir);

        var linkFile = ZSchemeHome.GetToolchainLinkFile(name, Home);
        if (!File.Exists(linkFile))
            return null;

        var target = ReadLinkTarget(linkFile);
        return target is null ? null : Linked(name, target);
    }

    /// <summary>True when the toolchain is a link whose target no longer exists.</summary>
    public static bool IsLinkBroken(InstalledToolchain toolchain)
    {
        return toolchain.IsLinked && !Directory.Exists(toolchain.Dir);
    }

    /// <summary>The default toolchain name, or <c>null</c> if none is set or settings are unreadable.</summary>
    public string? GetDefault()
    {
        return ReadSettings().DefaultToolchain;
    }

    public void SetDefault(string name)
    {
        var settings = ReadSettings();
        settings.DefaultToolchain = ToolchainName.Validate(name);
        WriteSettings(settings);
    }

    public void ClearDefault()
    {
        var settings = ReadSettings();
        settings.DefaultToolchain = null;
        WriteSettings(settings);
    }

    /// <summary>Registers a locally built tree as a selectable toolchain.</summary>
    public void Link(string name, string targetDir)
    {
        ToolchainName.Validate(name);

        var full = Path.GetFullPath(targetDir);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"No such directory: {full}");

        if (Directory.Exists(ZSchemeHome.GetToolchainDir(name, Home)))
            throw new IOException(
                $"'{name}' is an installed toolchain; pick another name or run `zsup uninstall {name}` first"
            );

        Directory.CreateDirectory(ZSchemeHome.GetToolchainsDir(Home));
        File.WriteAllText(ZSchemeHome.GetToolchainLinkFile(name, Home), full + Environment.NewLine);
    }

    /// <exception cref="DefaultNotClearedException">
    ///     The link was deleted, but the settings file still names it as the default.
    /// </exception>
    public void Unlink(string name)
    {
        var linkFile = ZSchemeHome.GetToolchainLinkFile(name, Home);
        if (!File.Exists(linkFile))
            throw new FileNotFoundException($"No linked toolchain named '{name}'");

        File.Delete(linkFile);
        ClearDefaultIf(name);
    }

    /// <summary>Removes an installed toolchain's directory and the link file if it is linked.</summary>
    /// <remarks>
    ///     Both, not either: <see cref="ToolchainInstaller.InstallFrom" /> refuses to create a name
    ///     that carries a directory and a <c>.link</c> at once, but its own <c>--force</c> path
    ///     deletes the link past the commit point and only warns when it cannot, so the collision
    ///     stays reachable. Removing just the directory would report success and leave the name
    ///     selectable through the link -- pointing at a build tree that has since moved on, or at
    ///     nothing at all.
    ///     <para>
    ///         The directory goes first so that a locked payload fails with the link still in place,
    ///         which is the state <c>zsup uninstall</c>'s "could not remove" path already describes.
    ///         The other order would delete the link and then throw, quietly changing what the name
    ///         resolves to on the way out.
    ///     </para>
    /// </remarks>
    /// <exception cref="DefaultNotClearedException">
    ///     The toolchain was removed, but the settings file still names it as the default.
    /// </exception>
    public void Remove(string name)
    {
        var dir = ZSchemeHome.GetToolchainDir(name, Home);
        var linkFile = ZSchemeHome.GetToolchainLinkFile(name, Home);
        var removed = false;

        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            removed = true;
        }

        if (File.Exists(linkFile))
        {
            File.Delete(linkFile);
            removed = true;
        }

        if (!removed)
            throw new DirectoryNotFoundException($"Toolchain '{name}' is not installed");

        ClearDefaultIf(name);
    }

    private void ClearDefaultIf(string name)
    {
        if (!ToolchainName.AreSame(GetDefault(), name))
            return;

        try
        {
            ClearDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Re-typed rather than left as-is: by this point the removal has already happened, and
            // the caller's error path for "could not remove" would otherwise claim a toolchain that
            // is gone is still installed.
            throw new DefaultNotClearedException(name, e);
        }
    }

    private InstalledToolchain Installed(string name, string dir)
    {
        return new InstalledToolchain(
            name,
            dir,
            ResolveBinDir(dir),
            IsLinked: false,
            LinkTargetPath: null
        );
    }

    private static InstalledToolchain Linked(string name, string target)
    {
        return new InstalledToolchain(
            name,
            target,
            ResolveBinDir(target),
            IsLinked: true,
            LinkTargetPath: target
        );
    }

    /// <summary>
    ///     Installed toolchains keep their binaries in <c>bin/</c>. A linked developer tree usually
    ///     points straight at a build output directory where <c>zs</c> sits at the root, so accept
    ///     both shapes.
    /// </summary>
    private static string ResolveBinDir(string toolchainDir)
    {
        var nested = Path.Combine(toolchainDir, "bin");
        if (File.Exists(Path.Combine(nested, ZSchemeHome.ExeName("zs"))))
            return nested;

        if (File.Exists(Path.Combine(toolchainDir, ZSchemeHome.ExeName("zs"))))
            return toolchainDir;

        // Neither exists yet (a broken link, or a partially written install). Report the
        // conventional location so error messages point somewhere meaningful.
        return nested;
    }

    private static string? ReadLinkTarget(string linkFile)
    {
        try
        {
            foreach (var line in File.ReadLines(linkFile))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                    return Path.GetFullPath(trimmed);
            }
        }
        catch (Exception e)
            when (e
                    is IOException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or NotSupportedException
                        or PathTooLongException
            )
        {
            // An unreadable or malformed link file behaves as if it were absent, rather than making
            // every command fail. GetFullPath throws ArgumentException on a malformed line, and
            // UnauthorizedAccessException is not an IOException.
        }

        return null;
    }

    private ToolchainSettings ReadSettings()
    {
        var path = ZSchemeHome.GetSettingsFile(Home);
        if (!File.Exists(path))
            return new ToolchainSettings();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, ToolchainJsonContext.Default.ToolchainSettings)
                ?? new ToolchainSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable settings file must not make zsup unusable — it degrades to
            // "no default", which every command already has an error path for. That includes
            // UnauthorizedAccessException, which is not an IOException and is what a settings.json
            // written by a `sudo zsup use` raises: this runs on the shim's hot path through
            // ToolchainResolver, so without it every `zs` invocation dies before it starts.
            return new ToolchainSettings();
        }
    }

    private void WriteSettings(ToolchainSettings settings)
    {
        settings.FormatVersion = ToolchainSettings.CurrentFormatVersion;

        Directory.CreateDirectory(Home);
        var path = ZSchemeHome.GetSettingsFile(Home);
        var json = JsonSerializer.Serialize(
            settings,
            ToolchainJsonContext.Default.ToolchainSettings
        );

        // Write-then-rename so an interrupted write cannot leave a truncated settings file. The
        // rename is atomic but the staging slot has to be private too, hence the per-process
        // suffix every other transient in the home already carries. Sharing one settings.json.tmp
        // lets two concurrent zsup processes interleave: one renames away the bytes the other
        // wrote -- reporting its own toolchain as the new default while the file says otherwise --
        // and the loser's rename then throws FileNotFoundException. Distinct slots make this a
        // genuine last-writer-wins, which is the right semantics for setting a default.
        SweepStaleStaging(path);

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllText(temp, json + Environment.NewLine);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            // Nothing else ever would: these sit in the home root, and SweepTransients only walks
            // downloads/. Without this, every write that failed before the rename leaks one.
            TryDeleteFile(temp);
        }
    }

    /// <summary>
    ///     Deletes settings staging files older than <see cref="ZSchemeHome.StagingMaxAge" />.
    /// </summary>
    /// <remarks>
    ///     The <c>finally</c> around the rename only covers a failure. A kill between the write and
    ///     the rename -- Ctrl-C, a power loss -- leaves a slot behind, and since every write now
    ///     stages under its own name they would accumulate in the home root forever rather than
    ///     being reused. Age-gated because the point of the private slot is that a concurrent zsup
    ///     has one too: unlinking a live one on Unix would leave that process renaming a path that
    ///     no longer exists, which is the race this staging scheme exists to remove.
    /// </remarks>
    private void SweepStaleStaging(string settingsPath)
    {
        var cutoff = DateTime.UtcNow - ZSchemeHome.StagingMaxAge;

        try
        {
            foreach (
                var stale in Directory.EnumerateFiles(
                    Home,
                    Path.GetFileName(settingsPath) + ".tmp-*"
                )
            )
                if (File.GetLastWriteTimeUtc(stale) < cutoff)
                    TryDeleteFile(stale);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best-effort. Leftover slots cost disk, not correctness, and must never be the reason
            // a default could not be recorded.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best-effort: the file is either already gone (the rename consumed it) or locked, and
            // a stale staging file affects nothing -- the next write stages under its own name.
        }
    }
}
