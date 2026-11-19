using Intellifer.Hub.Domain;

namespace Intellifer.Hub.Application;

public sealed record PlaneIntakeCreated(Guid IntakeId, Guid WorkItemId, int? SequenceId);

public sealed record PlaneProject(Guid Id, string Name, string Identifier, bool IntakeEnabled);

/// <summary>Plane's REST API. Only lists projects, creates intake items and adds comments.</summary>
public interface IPlaneGateway
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<PlaneProject>> ListProjectsAsync(CancellationToken cancellationToken);

    Task<PlaneIntakeCreated> CreateIntakeAsync(Guid projectId, string title, string descriptionHtml, CancellationToken cancellationToken);

    Task AddCommentAsync(Guid projectId, Guid workItemId, string commentHtml, string externalId, CancellationToken cancellationToken);

    /// <summary>The intake decision and workflow state of an item. Read-only.</summary>
    Task<PlaneItemStatus> GetIntakeStatusAsync(Guid projectId, Guid workItemId, CancellationToken cancellationToken);

    /// <summary>Attaches a file to a work item (also one still in intake). Returns the Plane attachment id.</summary>
    Task<Guid> UploadAttachmentAsync(
        Guid projectId,
        Guid workItemId,
        string fileName,
        string contentType,
        byte[] data,
        CancellationToken cancellationToken);
}

/// <summary>A queued message on its way to Plane, with what is known about its conversation.</summary>
public sealed record PlaneJob(
    Guid MessageId,
    Guid ConversationId,
    ChannelType Channel,
    string Sender,
    string? CustomerName,
    string? CustomerCompany,
    string? Subject,
    string Body,
    bool HasMedia,
    string? MediaSummary,
    DateTimeOffset ReceivedAt,
    int Attempts,
    Guid? WorkItemId,
    Guid? PlaneProjectId = null,
    string? ThreadKey = null,
    DateTimeOffset? ConversationLastInboundAt = null,
    bool PlaneClosed = false,
    DateTimeOffset? TicketLastMessageAt = null);

/// <summary>The Postgres queue of messages waiting to be sent to Plane.</summary>
public interface IPlaneQueue
{
    /// <summary>Due messages, oldest first. A conversation is skipped while an older message of it is waiting to retry.</summary>
    Task<IReadOnlyList<PlaneJob>> GetDueAsync(int take, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Records the new ticket and marks every message that went into it as sent.</summary>
    Task MarkIntakeCreatedAsync(
        IReadOnlyList<Guid> messageIds,
        Guid conversationId,
        Guid projectId,
        PlaneIntakeCreated created,
        DateTimeOffset now,
        OutboundDraft? acknowledgement,
        CancellationToken cancellationToken);

    Task MarkSentAsync(Guid messageId, DateTimeOffset now, CancellationToken cancellationToken);

    Task MarkFailedAsync(Guid messageId, string error, DateTimeOffset? retryAt, CancellationToken cancellationToken);

    /// <summary>Takes the message (and its images) out of the queue without sending it, keeping the reason.</summary>
    Task MarkSkippedAsync(Guid messageId, string reason, CancellationToken cancellationToken);
}
