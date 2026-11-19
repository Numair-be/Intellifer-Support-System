using System.Net;
using System.Text.RegularExpressions;

namespace Intellifer.Hub.Application;

public static partial class EmailBodyCleaner
{
    public static string ToPlain(string html)
    {
        var withBreaks = BreakPattern().Replace(html, "\n");
        withBreaks = ParagraphPattern().Replace(withBreaks, "\n");
        var stripped = TagPattern().Replace(withBreaks, "");
        return WebUtility.HtmlDecode(stripped);
    }

    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>(lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            // Gmail wraps a long "On <date> <name> <address> wrote:" over two or three lines.
            if (IsWrappedAttribution(lines, index))
                break;
            if (trimmed is "--" or "-- ")
                break;
            if (trimmed.StartsWith("-----Original Message-----", StringComparison.OrdinalIgnoreCase))
                break;
            if (trimmed.StartsWith("________________________________", StringComparison.Ordinal))
                break;
            if (WrotePattern().IsMatch(line))
                break;
            if (trimmed.StartsWith('>'))
                break;
            kept.Add(line);
        }

        return string.Join('\n', kept).Trim();
    }

    private static bool IsWrappedAttribution(string[] lines, int start)
    {
        if (!lines[start].TrimStart().StartsWith("On ", StringComparison.OrdinalIgnoreCase))
            return false;

        var joined = lines[start];
        for (var next = start + 1; next < lines.Length && next <= start + 2; next++)
        {
            joined += " " + lines[next].Trim();
            if (WrotePattern().IsMatch(joined))
                return true;
        }

        return false;
    }

    [GeneratedRegex("<br\\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakPattern();

    [GeneratedRegex("</p>", RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphPattern();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex("^\\s*On .+ wrote:\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex WrotePattern();
}
