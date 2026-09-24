using System.Text;
using System.Text.RegularExpressions;
using WinMux.Terminal;

namespace WinMux.Shell;

/// <summary>A link found in a terminal row, and the columns it covers.</summary>
/// <param name="Url">The target, always <c>http</c> or <c>https</c>.</param>
/// <param name="Start">First column of the link, inclusive.</param>
/// <param name="End">One past the last column of the link.</param>
public readonly record struct TerminalLink(string Url, int Start, int End);

/// <summary>
/// Finding the link under a cell, which is arithmetic over a row of cells and therefore testable
/// without a terminal, a pointer or a screen.
///
/// <para>
/// Two kinds, because terminals have two. A program may mark a link explicitly with OSC 8, and the
/// engine already carries the id on every cell it covers; that is exact, and it is how a link can
/// have text that is not its address. Everything else is a bare URL sitting in ordinary output,
/// found by looking at the characters — which is what `dir` of a text file, a compiler and a stack
/// trace all produce.
/// </para>
///
/// <para>
/// <b>Only http and https open.</b> A terminal shows the output of whatever is running in it, and
/// that output is not trusted: a <c>file:</c> URL is a local path, and a registered custom scheme
/// is an arbitrary program with an argument of the writer's choosing. Both are one line of output
/// away from being a way to run something by getting someone to click a word.
/// </para>
/// </summary>
public static class TerminalLinkModel
{
    /// <summary>
    /// Deliberately not a general URI grammar. It matches what a person would point at and call a
    /// link, and stops at whitespace and at the quotes and brackets that usually surround one.
    /// </summary>
    private static readonly Regex UrlPattern = new(
        @"https?://[^\s<>""'`\u0000]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Trailing characters that are nearly always the sentence around a link rather than part of
    /// it. A closing bracket is kept when the link opened one, so a URL containing <c>(v=1)</c>
    /// survives being written in prose.
    /// </summary>
    private const string TrailingPunctuation = ".,;:!?'\"";

    /// <summary>The link covering <paramref name="column"/>, or null.</summary>
    public static TerminalLink? At(ReadOnlySpan<TerminalCell> row, int column, Func<ushort, string?> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        if (column < 0 || column >= row.Length) return null;

        return Marked(row, column, resolve) ?? InText(row, column);
    }

    /// <summary>An OSC 8 hyperlink: the run of neighbouring cells carrying the same id.</summary>
    private static TerminalLink? Marked(ReadOnlySpan<TerminalCell> row, int column, Func<ushort, string?> resolve)
    {
        var id = row[column].HyperlinkId;
        if (id == 0) return null;
        if (resolve(id) is not { } url || !IsOpenable(url)) return null;

        var start = column;
        var end = column + 1;
        while (start > 0 && row[start - 1].HyperlinkId == id) start--;
        while (end < row.Length && row[end].HyperlinkId == id) end++;
        return new TerminalLink(url, start, end);
    }

    /// <summary>A URL written out in the text.</summary>
    private static TerminalLink? InText(ReadOnlySpan<TerminalCell> row, int column)
    {
        // Built with one character per cell so a match's index is a column. A cell holding a
        // multi-character grapheme is not part of a URL, and a wide character's trailing cell must
        // still occupy a column or everything after it would be off by one.
        var text = new StringBuilder(row.Length);
        for (var i = 0; i < row.Length; i++)
        {
            var cell = row[i];
            text.Append(cell.IsBlank || cell.IsWideTrailing || cell.ExtendedGlyph is not null || cell.Character == '\0'
                ? ' '
                : cell.Character);
        }

        foreach (var match in UrlPattern.EnumerateMatches(text.ToString().AsSpan()))
        {
            if (column < match.Index || column >= match.Index + match.Length) continue;

            var url = text.ToString(match.Index, match.Length);
            var trimmed = Trim(url);
            if (trimmed.Length == 0 || column >= match.Index + trimmed.Length) return null;
            return IsOpenable(trimmed) ? new TerminalLink(trimmed, match.Index, match.Index + trimmed.Length) : null;
        }

        return null;
    }

    internal static string Trim(string url)
    {
        var end = url.Length;
        while (end > 0)
        {
            var c = url[end - 1];
            if (TrailingPunctuation.Contains(c, StringComparison.Ordinal)) { end--; continue; }

            // A bracket closes only what the URL itself opened; otherwise it belongs to the prose.
            if (c is ')' or ']' or '}')
            {
                var open = c switch { ')' => '(', ']' => '[', _ => '{' };
                var depth = 0;
                for (var i = 0; i < end; i++)
                {
                    if (url[i] == open) depth++;
                    else if (url[i] == c) depth--;
                }

                if (depth < 0) { end--; continue; }
            }

            break;
        }

        return url[..end];
    }

    /// <summary>Whether WinMux will hand this to the system. See the type's remarks.</summary>
    public static bool IsOpenable(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
