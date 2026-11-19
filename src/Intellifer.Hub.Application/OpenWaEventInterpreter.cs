using System.Text.Json;
using System.Text.Json.Nodes;
using Intellifer.Hub.Domain;

namespace Intellifer.Hub.Application;

public abstract record OpenWaInterpretation
{
    public sealed record Inbound(InboundMessage Message) : OpenWaInterpretation;

    public sealed record Session(string Status, string Detail) : OpenWaInterpretation;

    public sealed record Ignored(string Reason) : OpenWaInterpretation;
}

/// <summary>
/// Reads webhooks from OpenWA by rmyndharis:
/// <c>{ "event": "message.received", "sessionId": ..., "idempotencyKey": ..., "data": { ... } }</c>.
/// </summary>
public sealed class OpenWaEventInterpreter
{
    private static readonly HashSet<string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image", "video", "audio", "ptt", "voice", "document", "sticker"
    };

    public OpenWaInterpretation Interpret(JsonElement root)
    {
        var eventName = ReadString(root, "event");
        if (string.IsNullOrWhiteSpace(eventName))
            return new OpenWaInterpretation.Ignored("missing event");

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return new OpenWaInterpretation.Ignored("missing data");

        return eventName.ToLowerInvariant() switch
        {
            "message.received" => InterpretMessage(data),
            // ready = linked and connected; anything else (qr_ready, disconnected, action_required ...) needs a look.
            "session.status" => Session(ReadString(data, "status") ?? ReadString(data, "state") ?? "unknown"),
            "session.authenticated" => new OpenWaInterpretation.Session("ok", "authenticated"),
            "session.disconnected" => new OpenWaInterpretation.Session(
                "attention",
                TextClip.Clip($"disconnected: {ReadString(data, "reason") ?? "no reason given"}", 500)),
            _ => new OpenWaInterpretation.Ignored(eventName)
        };
    }

    private static OpenWaInterpretation Session(string status) =>
        new OpenWaInterpretation.Session(
            string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase) ? "ok" : "attention",
            TextClip.Clip(status, 500));

    private static OpenWaInterpretation InterpretMessage(JsonElement message)
    {
        var id = ReadString(message, "id");
        var from = ReadString(message, "from");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(from))
            return new OpenWaInterpretation.Ignored("message is missing id or sender");
        if (id.Length > 512)
            return new OpenWaInterpretation.Ignored("message id is too long");
        if (ReadBool(message, "fromMe"))
            return new OpenWaInterpretation.Ignored("message was sent by the gateway");

        var chatId = ReadString(message, "chatId") ?? from;
        if (chatId.Length > 512)
            return new OpenWaInterpretation.Ignored("thread key is too long");
        var isGroup = ReadBool(message, "isGroup") || chatId.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);

        // In a group "from" is the group; the person is "author".
        var sender = isGroup ? ReadString(message, "author") ?? from : from;
        // WhatsApp may hide the number behind a private LID; OpenWA adds senderPhone when it can resolve it
        // (RESOLVE_LID_TO_PHONE=true).
        if (sender.EndsWith("@lid", StringComparison.OrdinalIgnoreCase)
            && new string((ReadString(message, "senderPhone") ?? "").Where(char.IsDigit).ToArray()) is { Length: >= 8 } phone)
            sender = phone + "@c.us";

        var type = ReadString(message, "type");
        var media = message.TryGetProperty("media", out var mediaValue) && mediaValue.ValueKind == JsonValueKind.Object
            ? mediaValue
            : (JsonElement?)null;
        var hasMedia = media is not null || (type is not null && MediaTypes.Contains(type));
        var text = ReadString(message, "body") ?? ReadString(message, "caption") ?? "";
        var receivedAt = ReadTimestamp(message);

        var mimetype = media is { } m ? ReadString(m, "mimetype") : null;
        var fileName = media is { } f ? ReadString(f, "filename") : null;
        var mediaParts = new[] { type, mimetype, fileName }.Where(part => !string.IsNullOrWhiteSpace(part));

        // Photos, and images sent as documents. Stickers are images too but are not evidence.
        var attachments = new List<InboundAttachment>();
        if (media is { } image && InboundAttachment.IsImage(mimetype) && !string.Equals(type, "sticker", StringComparison.OrdinalIgnoreCase))
            attachments.Add(ToAttachment(image, mimetype!, fileName, chatId, id, receivedAt));

        return new OpenWaInterpretation.Inbound(new InboundMessage
        {
            Channel = ChannelType.WhatsApp,
            ExternalMessageId = id,
            ThreadKey = chatId,
            SenderRaw = sender,
            SenderNormalized = sender,
            Body = text,
            HasMedia = hasMedia,
            MediaSummary = hasMedia ? string.Join(", ", mediaParts) : null,
            ReceivedAt = receivedAt,
            Attachments = attachments
        });
    }

    /// <summary>Small media comes inline as base64; larger media is fetched later through the gateway.</summary>
    private static InboundAttachment ToAttachment(
        JsonElement media,
        string mimetype,
        string? fileName,
        string chatId,
        string messageId,
        DateTimeOffset receivedAt)
    {
        var contentType = mimetype.Split(';')[0].Trim();
        var name = fileName ?? $"whatsapp-image-{receivedAt:yyyy-MM-dd-HHmmss}.{contentType.Split('/')[1]}";
        var size = media.TryGetProperty("sizeBytes", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) ? bytes : 0;

        var inline = ReadString(media, "data");
        if (inline is not null && !ReadBool(media, "omitted"))
        {
            try
            {
                var base64 = inline.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? inline[(inline.IndexOf(',') + 1)..] : inline;
                var data = Convert.FromBase64String(base64);
                return new InboundAttachment(name, contentType, data.LongLength, data);
            }
            catch (FormatException)
            {
                // Fall through and download it instead.
            }
        }

        var source = new JsonObject { ["chatId"] = chatId, ["messageId"] = messageId }.ToJsonString();
        return new InboundAttachment(name, contentType, size, Source: source);
    }

    private static DateTimeOffset ReadTimestamp(JsonElement message)
    {
        if (!message.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.Number)
            return DateTimeOffset.UtcNow;
        if (!timestamp.TryGetInt64(out var value) || value <= 0)
            return DateTimeOffset.UtcNow;
        return value > 10_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : DateTimeOffset.FromUnixTimeSeconds(value);
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static bool ReadBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false
        };
    }
}
