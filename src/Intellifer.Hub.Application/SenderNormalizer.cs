using System.Text.RegularExpressions;
using Intellifer.Hub.Application.Options;
using Intellifer.Hub.Domain;
using Microsoft.Extensions.Options;
using PhoneNumbers;

namespace Intellifer.Hub.Application;

public sealed partial class SenderNormalizer(IOptions<HubOptions> options) : ISenderNormalizer
{
    private static readonly PhoneNumberUtil PhoneNumbers = PhoneNumberUtil.GetInstance();

    public string Normalize(ChannelType channel, string senderRaw)
    {
        var raw = senderRaw.Trim();
        if (raw.Length == 0)
            return "unknown";

        return channel switch
        {
            ChannelType.Email => NormalizeEmail(raw),
            ChannelType.WhatsApp => NormalizeWhatsApp(raw),
            _ => raw
        };
    }

    private static string NormalizeEmail(string raw)
    {
        var match = EmailPattern().Match(raw);
        return match.Success ? match.Value.ToLowerInvariant() : raw.ToLowerInvariant();
    }

    private string NormalizeWhatsApp(string raw)
    {
        var at = raw.IndexOf('@');
        if (at > 0)
        {
            var domain = raw[at..];
            if (domain.Equals("@g.us", StringComparison.OrdinalIgnoreCase)
                || domain.Equals("@lid", StringComparison.OrdinalIgnoreCase)
                || domain.Equals("@broadcast", StringComparison.OrdinalIgnoreCase))
                return raw.ToLowerInvariant();

            if (domain.Equals("@c.us", StringComparison.OrdinalIgnoreCase)
                || domain.Equals("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase))
                raw = raw[..at];
        }

        return ToE164(raw);
    }

    private string ToE164(string raw)
    {
        var value = raw.Trim();
        if (value.StartsWith("00", StringComparison.Ordinal))
            value = "+" + value[2..];

        try
        {
            var region = string.IsNullOrWhiteSpace(options.Value.DefaultPhoneRegion)
                ? "IN"
                : options.Value.DefaultPhoneRegion;
            var parsed = PhoneNumbers.Parse(value, region);
            if (PhoneNumbers.IsValidNumber(parsed))
                return PhoneNumbers.Format(parsed, PhoneNumberFormat.E164);
        }
        catch (NumberParseException)
        {
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length is >= 8 and <= 15)
            return "+" + digits;

        return value.ToLowerInvariant();
    }

    [GeneratedRegex("[A-Z0-9._%+\\-]+@[A-Z0-9.\\-]+\\.[A-Z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex EmailPattern();
}
