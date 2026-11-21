using System.Text.RegularExpressions;
using Intellifer.Hub.Domain;

namespace Intellifer.Hub.Application;

public enum CustomerKeyKind
{
    Email = 1,
    Phone = 2
}

/// <summary>What to look a sender up by: a lowercased email, or an E.164 phone number.</summary>
public sealed record CustomerKey(CustomerKeyKind Kind, string Value)
{
    /// <summary>Digits at the end of the number, used to narrow the search in Odoo before exact matching.</summary>
    public string PhoneSuffix(int digits)
    {
        var all = new string(Value.Where(char.IsDigit).ToArray());
        return all.Length <= digits ? all : all[^digits..];
    }

    public static bool TryCreate(ChannelType channel, string senderNormalized, out CustomerKey key)
    {
        key = null!;
        var value = senderNormalized.Trim();
        switch (channel)
        {
            case ChannelType.Email when value.Contains('@') && !value.Contains(' '):
                key = new CustomerKey(CustomerKeyKind.Email, value.ToLowerInvariant());
                return true;
            // WhatsApp senders are E.164 (+919876543210). A private @lid id or a group id is not a phone number.
            case ChannelType.WhatsApp when value.StartsWith('+') && value.Length >= 9 && value[1..].All(char.IsDigit):
                key = new CustomerKey(CustomerKeyKind.Phone, value);
                return true;
            default:
                return false;
        }
    }
}

/// <summary>A client contact as read from Odoo, before deciding whether it really matches.</summary>
public sealed record ContactCandidate(
    int Id,
    string Name,
    string? CompanyName,
    IReadOnlyList<ClientProject> Projects,
    IReadOnlyList<string> Emails,
    IReadOnlyList<string> Phones);

/// <summary>
/// Picks the contacts that really have the sender's email or phone. Odoo is only asked for a wide set of
/// candidates, because phones are stored in free formats such as "+91 98765 43210" or "098765-43210".
/// </summary>
public sealed partial class CustomerMatcher(ISenderNormalizer normalizer)
{
    public CustomerMatch? Pick(CustomerKey key, IEnumerable<ContactCandidate> candidates)
    {
        var matches = candidates.Where(candidate => Matches(key, candidate)).OrderBy(candidate => candidate.Id).ToList();
        if (matches.Count == 0)
            return null;

        var first = matches[0];
        var projects = matches
            .SelectMany(candidate => candidate.Projects)
            .Where(project => !string.IsNullOrWhiteSpace(project.Name))
            .DistinctBy(project => project.Name.Trim().ToLowerInvariant())
            .ToList();
        var company = string.IsNullOrWhiteSpace(first.CompanyName) ? null : first.CompanyName.Trim();
        return new CustomerMatch(first.Id, first.Name.Trim(), company, projects);
    }

    private bool Matches(CustomerKey key, ContactCandidate candidate) => key.Kind switch
    {
        CustomerKeyKind.Email => candidate.Emails
            .SelectMany(field => EmailPattern().Matches(field).Select(match => match.Value))
            .Any(email => string.Equals(email, key.Value, StringComparison.OrdinalIgnoreCase)),
        CustomerKeyKind.Phone => candidate.Phones
            .Where(phone => !string.IsNullOrWhiteSpace(phone))
            .Any(phone => normalizer.Normalize(ChannelType.WhatsApp, phone) == key.Value),
        _ => false
    };

    [GeneratedRegex("[A-Z0-9._%+\\-]+@[A-Z0-9.\\-]+\\.[A-Z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex EmailPattern();
}
