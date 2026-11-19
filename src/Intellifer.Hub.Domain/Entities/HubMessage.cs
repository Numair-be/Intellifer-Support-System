namespace Intellifer.Hub.Domain.Entities;

public sealed class HubMessage
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public Conversation Conversation { get; set; } = null!;

    public Direction Direction { get; set; } = Direction.Inbound;

    public string ExternalMessageId { get; set; } = "";

    public string SenderRaw { get; set; } = "";

    public string SenderNormalized { get; set; } = "";

    public string? Subject { get; set; }

    public string Body { get; set; } = "";

    public string? RawBody { get; set; }

    public bool HasMedia { get; set; }

    public string? MediaSummary { get; set; }

    public string? CorrelationId { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset IngestedAt { get; set; }

    public PlaneSyncState? PlaneState { get; set; }

    public int PlaneAttempts { get; set; }

    public DateTimeOffset? PlaneNextAttemptAt { get; set; }

    public string? PlaneError { get; set; }

    public DateTimeOffset? PlaneSentAt { get; set; }

    public List<MessageAttachment> Attachments { get; set; } = [];
}
