namespace Intellifer.Hub.Application.Options;

public sealed class OdooOptions
{
    public const string Section = "Odoo";

    /// <summary>Base URL of the Odoo server, e.g. https://erp.example.com. No trailing /jsonrpc.</summary>
    public string? Url { get; set; }

    public string? Database { get; set; }

    public string? Login { get; set; }

    /// <summary>An Odoo API key (Preferences → Account Security) or the user's password.</summary>
    public string? ApiKey { get; set; }

    public int TimeoutSeconds { get; set; } = 10;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Url)
        && !string.IsNullOrWhiteSpace(Database)
        && !string.IsNullOrWhiteSpace(Login)
        && !string.IsNullOrWhiteSpace(ApiKey);
}
