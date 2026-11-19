namespace Intellifer.Hub.Application.Options;

public sealed class GmailOptions
{
    public const string Section = "Gmail";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string? RefreshToken { get; set; }

    public string Mailbox { get; set; } = "me";

    public int PollIntervalSeconds { get; set; } = 45;

    public int BootstrapDays { get; set; } = 7;

    public int BootstrapMaxMessages { get; set; } = 50;

    public string ProcessedLabel { get; set; } = "Support";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(RefreshToken);
}
