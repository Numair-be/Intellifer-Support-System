namespace Intellifer.Hub.Domain;

public sealed record InboundMessage
{
    public required ChannelType Channel { get; init; }

    public required string ExternalMessageId { get; init; }

    public required string ThreadKey { get; init; }

    public required string SenderRaw { get; init; }

    public required string SenderNormalized { get; init; }

    public string? Subject { get; init; }

    public required string Body { get; init; }

    public string? RawBody { get; init; }

    public bool HasMedia { get; init; }

    public string? MediaSummary { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>Set by intake when the message reads like a bug or error report for Plane.</summary>
    public bool FlaggedAsIssue { get; init; }

    /// <summary>
    /// Set by intake when Plane is set up. The message then waits in the Plane queue, where it is judged
    /// together with the rest of its burst of messages.
    /// </summary>
    public bool QueueForPlane { get; init; }

    /// <summary>Images that came with the message, carried over to the Plane ticket.</summary>
    public IReadOnlyList<InboundAttachment> Attachments { get; init; } = [];
}

/// <summary>An image on an inbound message: either the file itself, or what the channel needs to fetch it later.</summary>
public sealed record InboundAttachment(
    string FileName,
    string ContentType,
    long Size,
    byte[]? Data = null,
    string? Source = null,
    string? SkipReason = null)
{
    public static bool IsImage(string? contentType) =>
        contentType is not null && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}
