using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Intellifer.Hub.Application;
using Intellifer.Hub.Application.Options;
using Intellifer.Hub.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intellifer.Hub.Infrastructure.OpenWa;

public sealed class OpenWaApiException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}

/// <summary>
/// OpenWA by rmyndharis (REST API, X-API-Key). One session, named in settings, with one webhook to the hub.
/// Singleton, so the session id is looked up once.
/// </summary>
public sealed class OpenWaGateway(
    IHttpClientFactory httpClients,
    IOptions<OpenWaOptions> options,
    ILogger<OpenWaGateway> logger) : IWhatsAppGateway, IOpenWaSessionProbe
{
    public const string HttpClientName = "openwa";

    // What the hub needs to hear about. Session events feed /health/openwa.
    private static readonly string[] WebhookEvents =
        ["message.received", "session.status", "session.authenticated", "session.disconnected"];

    private readonly SemaphoreSlim _setupLock = new(1, 1);
    private string? _sessionId;
    private bool _webhookChecked;

    public bool IsConfigured => options.Value.IsConfigured;

    public async Task<WhatsAppSessionState> EnsureSetupAsync(CancellationToken cancellationToken)
    {
        await _setupLock.WaitAsync(cancellationToken);
        try
        {
            var state = await FindOrCreateSessionAsync(cancellationToken);
            if (!_webhookChecked)
            {
                await EnsureWebhookAsync(state.SessionId, cancellationToken);
                _webhookChecked = true;
            }

            return state;
        }
        finally
        {
            _setupLock.Release();
        }
    }

    public async Task<WhatsAppSessionState> GetStateAsync(CancellationToken cancellationToken)
    {
        var sessionId = await SessionIdAsync(cancellationToken);
        using var document = await SendAsync(HttpMethod.Get, $"/api/sessions/{sessionId}", null, cancellationToken);
        return ToState(document.RootElement);
    }

    public async Task<WhatsAppLinkResult> LinkAsync(string? phoneNumber, CancellationToken cancellationToken)
    {
        var state = await EnsureSetupAsync(cancellationToken);
        if (state.IsReady)
            return new WhatsAppLinkResult(state.Status, null, null);

        // Start the engine if it is not already waiting for a link, then wait for it to be ready to pair.
        if (!string.Equals(state.Status, "qr_ready", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var _ = await SendAsync(HttpMethod.Post, $"/api/sessions/{state.SessionId}/start", null, cancellationToken);
            }
            catch (OpenWaApiException exception) when (exception.StatusCode is 409)
            {
                // Already starting.
            }

            for (var i = 0; i < 45 && !string.Equals(state.Status, "qr_ready", StringComparison.OrdinalIgnoreCase); i++)
            {
                if (state.IsReady)
                    return new WhatsAppLinkResult(state.Status, null, null);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                state = await GetStateAsync(cancellationToken);
            }
        }

        var digits = new string((phoneNumber ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length > 0)
        {
            using var document = await SendAsync(
                HttpMethod.Post,
                $"/api/sessions/{state.SessionId}/pairing-code",
                new JsonObject { ["phoneNumber"] = digits },
                cancellationToken);
            return new WhatsAppLinkResult(
                Text(document.RootElement, "status") ?? state.Status,
                Text(document.RootElement, "pairingCode"),
                null);
        }

        using (var document = await SendAsync(HttpMethod.Get, $"/api/sessions/{state.SessionId}/qr", null, cancellationToken))
            return new WhatsAppLinkResult(Text(document.RootElement, "status") ?? state.Status, null, Text(document.RootElement, "qrCode"));
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        var sessionId = await SessionIdAsync(cancellationToken);
        using var _ = await SendAsync(HttpMethod.Post, $"/api/sessions/{sessionId}/logout", null, cancellationToken);
    }

    public async Task<string?> SendTextAsync(string chatId, string text, CancellationToken cancellationToken)
    {
        var sessionId = await SessionIdAsync(cancellationToken);
        using var document = await SendAsync(
            HttpMethod.Post,
            $"/api/sessions/{sessionId}/messages/send-text",
            new JsonObject { ["chatId"] = chatId, ["text"] = text },
            cancellationToken);
        return Text(document.RootElement, "messageId") ?? Text(document.RootElement, "id");
    }

    public async Task<(byte[] Data, string ContentType)> DownloadMediaAsync(
        string chatId,
        string messageId,
        CancellationToken cancellationToken)
    {
        var sessionId = await SessionIdAsync(cancellationToken);
        using var request = Request(
            HttpMethod.Get,
            $"/api/sessions/{sessionId}/messages/{Uri.EscapeDataString(chatId)}/{Uri.EscapeDataString(messageId)}/media",
            null);
        using var timeout = Timeout(cancellationToken, 120);
        using var response = await Client.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw await ErrorAsync(response, "download media", timeout.Token);

        var data = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return (data, contentType);
    }

    /// <summary>For /health/openwa: reachable, and whether the session is linked and ready.</summary>
    public async Task<OpenWaSessionStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            return new OpenWaSessionStatus(false, false, null, null, "Set OPENWA_URL and WA_API_KEY.");

        try
        {
            using var timeout = Timeout(cancellationToken, 5);
            var state = await GetStateAsync(timeout.Token);
            return new OpenWaSessionStatus(true, true, state.IsReady, state.IsReady, $"Session {options.Value.SessionName} is {state.Status}.");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("OpenWA probe failed: {Error}", exception.Message);
            return new OpenWaSessionStatus(true, false, null, null, "OpenWA did not answer.");
        }
    }

    private async Task<string> SessionIdAsync(CancellationToken cancellationToken) =>
        _sessionId ?? (await EnsureSetupAsync(cancellationToken)).SessionId;

    private async Task<WhatsAppSessionState> FindOrCreateSessionAsync(CancellationToken cancellationToken)
    {
        var name = options.Value.SessionName.Trim();
        using (var list = await SendAsync(HttpMethod.Get, $"/api/sessions?name={Uri.EscapeDataString(name)}", null, cancellationToken))
        {
            foreach (var session in Rows(list.RootElement))
            {
                if (string.Equals(Text(session, "name"), name, StringComparison.Ordinal))
                {
                    var found = ToState(session);
                    _sessionId = found.SessionId;
                    return found;
                }
            }
        }

        using var created = await SendAsync(HttpMethod.Post, "/api/sessions", new JsonObject { ["name"] = name }, cancellationToken);
        var state = ToState(created.RootElement);
        _sessionId = state.SessionId;
        logger.LogInformation("Created WhatsApp session {Name} ({SessionId}) in OpenWA", name, state.SessionId);
        return state;
    }

    /// <summary>One webhook to the hub, carrying the shared secret header. Re-sent on each start so a new secret applies.</summary>
    private async Task EnsureWebhookAsync(string sessionId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var body = new JsonObject
        {
            ["url"] = settings.HubWebhookUrl,
            ["events"] = new JsonArray(WebhookEvents.Select(name => (JsonNode)name).ToArray()),
            ["headers"] = new JsonObject { ["X-Webhook-Secret"] = settings.WebhookSecret ?? "" },
            ["retryCount"] = 5
        };

        string? existing = null;
        using (var list = await SendAsync(HttpMethod.Get, $"/api/sessions/{sessionId}/webhooks", null, cancellationToken))
        {
            existing = Rows(list.RootElement)
                .Where(hook => string.Equals(Text(hook, "url"), settings.HubWebhookUrl, StringComparison.OrdinalIgnoreCase))
                .Select(hook => Text(hook, "id"))
                .FirstOrDefault();
        }

        if (existing is null)
        {
            using var _ = await SendAsync(HttpMethod.Post, $"/api/sessions/{sessionId}/webhooks", body, cancellationToken);
            logger.LogInformation("Registered the hub webhook {Url} in OpenWA", settings.HubWebhookUrl);
        }
        else
        {
            using var _ = await SendAsync(HttpMethod.Put, $"/api/sessions/{sessionId}/webhooks/{existing}", body, cancellationToken);
        }
    }

    private HttpClient Client => httpClients.CreateClient(HttpClientName);

    private HttpRequestMessage Request(HttpMethod method, string path, JsonObject? body)
    {
        var settings = options.Value;
        var request = new HttpRequestMessage(method, settings.BaseUrl!.TrimEnd('/') + path);
        request.Headers.Add("X-API-Key", settings.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // A plain string body carries Content-Length (some proxies drop chunked bodies).
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            throw new OpenWaApiException("OpenWA is not configured. Set OPENWA_URL and WA_API_KEY.");

        using var request = Request(method, path, body);
        using var timeout = Timeout(cancellationToken, 30);
        using var response = await Client.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw await ErrorAsync(response, $"{method} {path.Split('?')[0]}", timeout.Token);

        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private static async Task<OpenWaApiException> ErrorAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var hint = (int)response.StatusCode switch
        {
            401 or 403 => " Check WA_API_KEY.",
            409 => " The session is not ready: is WhatsApp linked?",
            _ => ""
        };
        return new OpenWaApiException(
            $"OpenWA {what} returned {(int)response.StatusCode} {response.ReasonPhrase}.{hint} {TextClip.Clip(text, 300)}".Trim(),
            (int)response.StatusCode);
    }

    private static CancellationTokenSource Timeout(CancellationToken cancellationToken, int seconds)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(seconds));
        return source;
    }

    private static WhatsAppSessionState ToState(JsonElement session) => new(
        Text(session, "id") ?? throw new OpenWaApiException("OpenWA returned a session without an id."),
        Text(session, "status") ?? "unknown",
        Text(session, "phone"));

    // Lists come back as a bare array, or wrapped in data/items.
    private static IEnumerable<JsonElement> Rows(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().ToList();
        foreach (var name in new[] { "data", "items", "sessions", "webhooks" })
        {
            if (root.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.Array)
                return inner.EnumerateArray().ToList();
        }

        return [];
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>Replies on WhatsApp, to the chat the customer wrote from.</summary>
public sealed class OpenWaReplySender(IWhatsAppGateway gateway) : IReplySender
{
    public ChannelType Channel => ChannelType.WhatsApp;

    public bool IsConfigured => gateway.IsConfigured;

    public Task<string?> SendAsync(OutboundJob reply, CancellationToken cancellationToken) =>
        gateway.SendTextAsync(reply.Recipient, reply.Body, cancellationToken);
}

/// <summary>Fetches WhatsApp images that were too large to come inline with the webhook.</summary>
public sealed class OpenWaMediaClient(IWhatsAppGateway gateway) : IWhatsAppMedia
{
    public bool IsConfigured => gateway.IsConfigured;

    /// <summary><paramref name="messageJson"/> is {"chatId": ..., "messageId": ...}, recorded by the webhook interpreter.</summary>
    public Task<(byte[] Data, string ContentType)> DownloadAsync(string messageJson, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(messageJson);
        var root = document.RootElement;
        var chatId = root.TryGetProperty("chatId", out var chat) ? chat.GetString() : null;
        var messageId = root.TryGetProperty("messageId", out var message) ? message.GetString() : null;
        if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(messageId))
            throw new InvalidOperationException("The image has no chat and message id to download it with.");
        return gateway.DownloadMediaAsync(chatId, messageId, cancellationToken);
    }
}
