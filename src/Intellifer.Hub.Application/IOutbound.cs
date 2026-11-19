using Intellifer.Hub.Domain;

namespace Intellifer.Hub.Application;

/// <summary>A reply waiting to go out.</summary>
public sealed record OutboundJob(
    Guid Id,
    Guid ConversationId,
    ChannelType Channel,
    string Recipient,
    string ThreadKey,
    string? Subject,
    string Body,
    int Attempts);

/// <summary>A reply to queue: who, where, and what.</summary>
public sealed record OutboundDraft(
    Guid ConversationId,
    ChannelType Channel,
    string Recipient,
    string ThreadKey,
    string? Subject,
    string Body,
    string Reason);

/// <summary>Sends a reply on one channel. Returns the channel's id for the sent message, when it gives one.</summary>
public interface IReplySender
{
    ChannelType Channel { get; }

    bool IsConfigured { get; }

    Task<string?> SendAsync(OutboundJob reply, CancellationToken cancellationToken);
}

/// <summary>The Postgres queue of replies to customers.</summary>
public interface IOutboundQueue
{
    Task<IReadOnlyList<OutboundJob>> GetDueAsync(int take, DateTimeOffset now, CancellationToken cancellationToken);

    Task MarkSentAsync(Guid id, string? externalId, DateTimeOffset now, CancellationToken cancellationToken);

    Task MarkFailedAsync(Guid id, string error, DateTimeOffset? retryAt, CancellationToken cancellationToken);
}

/// <summary>A conversation whose Plane intake item is still open.</summary>
public sealed record TrackedIntake(
    Guid ConversationId,
    ChannelType Channel,
    string ThreadKey,
    string Participant,
    string? CustomerName,
    string? Subject,
    Guid ProjectId,
    Guid WorkItemId,
    int? SequenceId,
    string? StatusKey);

public interface IPlaneStatusStore
{
    /// <summary>Open items not checked since <paramref name="checkedBefore"/>, least recently checked first.</summary>
    Task<IReadOnlyList<TrackedIntake>> GetDueAsync(
        int take,
        DateTimeOffset checkedBefore,
        Guid? defaultProjectId,
        CancellationToken cancellationToken);

    /// <summary>Records the status and, in the same transaction, queues the reply when there is one.</summary>
    Task SaveAsync(
        Guid conversationId,
        string statusKey,
        bool closed,
        DateTimeOffset now,
        OutboundDraft? reply,
        CancellationToken cancellationToken);
}
