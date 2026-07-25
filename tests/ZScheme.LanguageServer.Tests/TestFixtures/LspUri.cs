using OmniSharp.Extensions.LanguageServer.Protocol;

namespace ZScheme.LanguageServer.Tests.TestFixtures;

/// <summary>
///     Spells URIs and paths the way the language server spells them.
/// </summary>
/// <remarks>
///     <see cref="DocumentUri" /> is a port of vscode-uri and lower-cases the Windows drive letter
///     in both <c>GetFileSystemPath()</c> and <c>ToString()</c>; <see cref="Uri" /> preserves it.
///     So a test that built its expectation with <c>new Uri(path).AbsoluteUri</c> compared
///     <c>file:///C:/…</c> against the server's <c>file:///c:/…</c> and failed on Windows while
///     passing everywhere else. The lower-cased form is the canonical one real clients send, so
///     expectations go through here rather than the handlers being changed to preserve casing.
///     <para>
///         Every URI in this test project is built here, including ones only ever used as
///         inputs — <c>new Uri(path).AbsoluteUri</c> passes until the day something compares it
///         against server output, and then fails on Windows only.
///     </para>
/// </remarks>
internal static class LspUri
{
    /// <summary>The URI string for <paramref name="path" />, as the server emits it.</summary>
    public static string Of(string path) => DocumentUri.FromFileSystemPath(path).ToString();

    /// <summary>The file-system path for <paramref name="path" />, as the server emits it.</summary>
    public static string PathOf(string path) =>
        DocumentUri.FromFileSystemPath(path).GetFileSystemPath();
}
