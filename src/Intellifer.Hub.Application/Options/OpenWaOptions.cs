namespace Intellifer.Hub.Application.Options;

/// <summary>The WhatsApp gateway: OpenWA by rmyndharis (https://github.com/rmyndharis/OpenWA).</summary>
public sealed class OpenWaOptions
{
    public const string Section = "OpenWa";

    /// <summary>Sent by the gateway on every webhook as X-Webhook-Secret; the hub rejects posts without it.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>The gateway's address as the hub sees it, e.g. http://openwa:2785.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The gateway's admin API key (WA_API_KEY; the gateway is started with it as API_MASTER_KEY).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Name of the hub's session in the gateway. Created on first use.</summary>
    public string SessionName { get; set; } = "support";

    /// <summary>Where the gateway posts events: the hub's webhook as the gateway sees it.</summary>
    public string HubWebhookUrl { get; set; } = "http://hub-api:8080/webhooks/openwa";

    /// <summary>Default number for pairing-code links: digits only, country code first. Empty means QR code.</summary>
    public string? LinkPhone { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}
