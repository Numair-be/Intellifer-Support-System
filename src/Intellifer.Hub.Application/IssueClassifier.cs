using System.Text.RegularExpressions;
using Intellifer.Hub.Application.Options;
using Microsoft.Extensions.Options;

namespace Intellifer.Hub.Application;

/// <summary>
/// Decides whether a message reads like a bug or error report. Keyword based on purpose: it is predictable,
/// and anything it flags lands in Plane's intake for a person to accept or decline.
/// </summary>
public sealed class IssueClassifier
{
    private readonly Regex? _pattern;

    public IssueClassifier(IOptions<PlaneOptions> options)
    {
        var phrases = (options.Value.IntakeKeywords ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // Allow any run of spaces inside a phrase, and match whole words only ("error" not "terror").
            .Select(phrase => string.Join(@"\s+", phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape)))
            .ToList();
        if (phrases.Count > 0)
            _pattern = new Regex(
                $@"(?<![\p{{L}}\p{{N}}])(?:{string.Join('|', phrases)})(?![\p{{L}}\p{{N}}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));
    }

    public bool IsIssue(string? subject, string? body)
    {
        if (_pattern is null)
            return false;

        // Curly apostrophes from phones would otherwise miss "doesn't work".
        var text = $"{subject}\n{body}".Replace('’', '\'');
        try
        {
            return _pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
