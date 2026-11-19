namespace Intellifer.Hub.Application;

/// <summary>The hub's WhatsApp session in the gateway: its id there and its state (ready, qr_ready, disconnected, ...).</summary>
public sealed record WhatsAppSessionState(string SessionId, string Status, string? Phone)
{
    public bool IsReady => string.Equals(Status, "ready", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What to show the person linking the phone: a pairing code, or a QR code.</summary>
public sealed record WhatsAppLinkResult(string Status, string? PairingCode, string? QrCode);

/// <summary>
/// The WhatsApp gateway (OpenWA by rmyndharis). The hub keeps one session there, named in settings, and one
/// webhook on it that posts to the hub. The gateway keeps the login on disk, so restarts do not need a new link.
/// </summary>
public interface IWhatsAppGateway
{
    bool IsConfigured { get; }

    /// <summary>Creates the session if it does not exist and makes sure its webhook points at the hub.</summary>
    Task<WhatsAppSessionState> EnsureSetupAsync(CancellationToken cancellationToken);

    Task<WhatsAppSessionState> GetStateAsync(CancellationToken cancellationToken);

    /// <summary>Starts linking: a pairing code for <paramref name="phoneNumber"/>, or a QR code when it is empty.</summary>
    Task<WhatsAppLinkResult> LinkAsync(string? phoneNumber, CancellationToken cancellationToken);

    /// <summary>Unlinks the device from the phone's account and stops the session.</summary>
    Task LogoutAsync(CancellationToken cancellationToken);

    Task<string?> SendTextAsync(string chatId, string text, CancellationToken cancellationToken);

    Task<(byte[] Data, string ContentType)> DownloadMediaAsync(string chatId, string messageId, CancellationToken cancellationToken);
}
