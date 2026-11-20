namespace Intellifer.Hub.Domain.Entities;

public enum TeamNotificationState
{
    Pending = 1,

    /// <summary>Messages to the team are queued (see outbound_messages), or there was nobody to tell.</summary>
    Done = 2,

    /// <summary>Gave up after the maximum number of attempts at looking up the team.</summary>
    Failed = 3
}

/// <summary>Tells the employees on a client project that a new intake item was created for it.</summary>
public sealed class TeamNotification
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public Conversation Conversation { get; set; } = null!;

    /// <summary>The Odoo client project, used to look up its team.</summary>
    public string ClientProject { get; set; } = "";

    public string? ClientProjectCode { get; set; }

    /// <summary>The Plane project name, shown in the message.</summary>
    public string PlaneProject { get; set; } = "";

    public Guid PlaneProjectId { get; set; }

    /// <summary>e.g. PLANE-12.</summary>
    public string TicketNumber { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>Who reported it, e.g. "Numair (Intellifer)".</summary>
    public string Customer { get; set; } = "";

    /// <summary>The customer's email or phone, so a team member who is also the customer is not messaged.</summary>
    public string CustomerKey { get; set; } = "";

    public TeamNotificationState State { get; set; } = TeamNotificationState.Pending;

    public int Attempts { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>Error while retrying, or a note when done (e.g. "no team members found").</summary>
    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
