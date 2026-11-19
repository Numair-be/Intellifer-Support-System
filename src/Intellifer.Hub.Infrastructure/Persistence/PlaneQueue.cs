using Intellifer.Hub.Application;
using Intellifer.Hub.Domain;
using Intellifer.Hub.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Intellifer.Hub.Infrastructure.Persistence;

public sealed class PlaneQueue(HubDbContext db) : IPlaneQueue
{
    public async Task<IReadOnlyList<PlaneJob>> GetDueAsync(int take, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return await db.Messages
            .Where(message => message.PlaneState == PlaneSyncState.Pending
                && (message.PlaneNextAttemptAt == null || message.PlaneNextAttemptAt <= now)
                // Keep each conversation in order: wait while an older message of it is backing off.
                && !db.Messages.Any(older => older.ConversationId == message.ConversationId
                    && older.PlaneState == PlaneSyncState.Pending
                    && older.ReceivedAt < message.ReceivedAt
                    && older.PlaneNextAttemptAt > now))
            .OrderBy(message => message.ReceivedAt)
            .ThenBy(message => message.IngestedAt)
            .Take(take)
            .Select(message => new PlaneJob(
                message.Id,
                message.ConversationId,
                message.Conversation.Channel,
                message.SenderNormalized,
                message.Conversation.CustomerName,
                message.Conversation.CustomerCompany,
                message.Subject,
                message.Body,
                message.HasMedia,
                message.MediaSummary,
                message.ReceivedAt,
                message.PlaneAttempts,
                message.Conversation.PlaneWorkItemId,
                message.Conversation.PlaneProjectId,
                message.Conversation.ThreadKey,
                message.Conversation.LastInboundAt,
                message.Conversation.PlaneClosed,
                db.Messages
                    .Where(other => other.ConversationId == message.ConversationId && other.PlaneState == PlaneSyncState.Sent)
                    .Max(other => (DateTimeOffset?)other.ReceivedAt)))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task MarkIntakeCreatedAsync(
        IReadOnlyList<Guid> messageIds,
        Guid conversationId,
        Guid projectId,
        PlaneIntakeCreated created,
        DateTimeOffset now,
        OutboundDraft? acknowledgement,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Conversations
                // Replaces the chat's previous ticket when that one was closed or idle; status replies now follow this one.
                .Where(conversation => conversation.Id == conversationId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(conversation => conversation.PlaneStatusKey, (string?)null)
                    .SetProperty(conversation => conversation.PlaneStatusCheckedAt, (DateTimeOffset?)null)
                    .SetProperty(conversation => conversation.PlaneClosed, false)
                    .SetProperty(conversation => conversation.PlaneProjectId, projectId)
                    .SetProperty(conversation => conversation.PlaneIntakeId, created.IntakeId)
                    .SetProperty(conversation => conversation.PlaneWorkItemId, created.WorkItemId)
                    .SetProperty(conversation => conversation.PlaneSequenceId, created.SequenceId),
                    cancellationToken);
            foreach (var messageId in messageIds)
                await MarkSentCoreAsync(messageId, now, cancellationToken);
            // Queued with the intake record, so the customer gets exactly one acknowledgement per ticket.
            if (acknowledgement is not null)
            {
                db.ChangeTracker.Clear();
                db.OutboundMessages.Add(new OutboundMessage
                {
                    Id = Guid.NewGuid(),
                    ConversationId = acknowledgement.ConversationId,
                    Channel = acknowledgement.Channel,
                    Recipient = acknowledgement.Recipient,
                    ThreadKey = acknowledgement.ThreadKey,
                    Subject = acknowledgement.Subject,
                    Body = acknowledgement.Body,
                    Reason = acknowledgement.Reason,
                    State = OutboundState.Pending,
                    CreatedAt = now
                });
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        });
    }

    public Task MarkSentAsync(Guid messageId, DateTimeOffset now, CancellationToken cancellationToken) =>
        MarkSentCoreAsync(messageId, now, cancellationToken);

    public async Task MarkFailedAsync(Guid messageId, string error, DateTimeOffset? retryAt, CancellationToken cancellationToken)
    {
        var state = retryAt is null ? PlaneSyncState.Failed : PlaneSyncState.Pending;
        await db.Messages
            .Where(message => message.Id == messageId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(message => message.PlaneState, state)
                .SetProperty(message => message.PlaneAttempts, message => message.PlaneAttempts + 1)
                .SetProperty(message => message.PlaneError, error)
                .SetProperty(message => message.PlaneNextAttemptAt, retryAt),
                cancellationToken);
    }

    public async Task MarkSkippedAsync(Guid messageId, string reason, CancellationToken cancellationToken)
    {
        PlaneSyncState? skipped = PlaneSyncState.Skipped;
        var text = TextClip.Clip(reason, 1_000);
        await db.Messages
            .Where(message => message.Id == messageId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(message => message.PlaneState, skipped)
                .SetProperty(message => message.PlaneError, text)
                .SetProperty(message => message.PlaneNextAttemptAt, (DateTimeOffset?)null),
                cancellationToken);

        // Its images will not go anywhere either: drop them from the hub.
        await db.Attachments
            .Where(attachment => attachment.MessageId == messageId
                && (attachment.State == AttachmentState.PendingDownload || attachment.State == AttachmentState.Ready))
            .ExecuteUpdateAsync(update => update
                .SetProperty(attachment => attachment.State, AttachmentState.Skipped)
                .SetProperty(attachment => attachment.Error, "The message did not go to a Plane ticket.")
                .SetProperty(attachment => attachment.Data, (byte[]?)null)
                .SetProperty(attachment => attachment.Source, (string?)null),
                cancellationToken);
    }

    private Task<int> MarkSentCoreAsync(Guid messageId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        PlaneSyncState? sent = PlaneSyncState.Sent;
        return db.Messages
            .Where(message => message.Id == messageId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(message => message.PlaneState, sent)
                .SetProperty(message => message.PlaneSentAt, now)
                .SetProperty(message => message.PlaneError, (string?)null)
                .SetProperty(message => message.PlaneNextAttemptAt, (DateTimeOffset?)null),
                cancellationToken);
    }
}
