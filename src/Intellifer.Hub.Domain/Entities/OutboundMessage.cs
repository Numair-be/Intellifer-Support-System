namespace Intellifer.Hub.Domain.Entities;

public enum OutboundState
{
    Pending = 1,
    Sent = 2,

    /// <summary>Gave up after the maximum number of attempts (dead letter).</summary>
    Failed = 3
}

/// <summary>A reply the hub sends to a customer, queued so it survives restarts and channel outages.</summary>
public sealed class OutboundMessage
{
    public Guid Id { get; set; }

    /// <summary>The customer conversation; null for messages not tied to one (e.g. telling the team about an app error).</summary>
    public Guid? ConversationId { get; set; }

    public Conversation? Conversation { get; set; }

    public ChannelType Channel { get; set; }

    /// <summary>Email address, or WhatsApp chat id.</summary>
    public string Recipient { get; set; } = "";

    /// <summary>Gmail thread id or WhatsApp chat id the reply belongs to.</summary>
    public string ThreadKey { get; set; } = "";

    public string? Subject { get; set; }

    public string Body { get; set; } = "";

    /// <summary>Why it was sent, e.g. "plane-status:accepted:backlog".</summary>
    public string Reason { get; set; } = "";

    public OutboundState State { get; set; } = OutboundState.Pending;

    public int Attempts { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    public string? Error { get; set; }

    public string? ExternalMessageId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }
}
