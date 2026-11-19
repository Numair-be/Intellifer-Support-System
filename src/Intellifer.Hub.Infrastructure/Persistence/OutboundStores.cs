using Intellifer.Hub.Application;
using Intellifer.Hub.Domain;
using Intellifer.Hub.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Intellifer.Hub.Infrastructure.Persistence;

public sealed class PlaneStatusStore(HubDbContext db) : IPlaneStatusStore
{
    public async Task<IReadOnlyList<TrackedIntake>> GetDueAsync(
        int take,
        DateTimeOffset checkedBefore,
        Guid? defaultProjectId,
        CancellationToken cancellationToken)
    {
        var rows = await db.Conversations
            .Where(conversation => conversation.PlaneWorkItemId != null
                && !conversation.PlaneClosed
                && (conversation.PlaneStatusCheckedAt == null || conversation.PlaneStatusCheckedAt < checkedBefore))
            .OrderBy(conversation => conversation.PlaneStatusCheckedAt ?? DateTimeOffset.MinValue)
            .Take(take)
            .Select(conversation => new
            {
                conversation.Id,
                conversation.Channel,
                conversation.ThreadKey,
                conversation.ParticipantNormalized,
                conversation.CustomerName,
                conversation.Subject,
                conversation.PlaneProjectId,
                WorkItemId = conversation.PlaneWorkItemId!.Value,
                conversation.PlaneSequenceId,
                conversation.PlaneStatusKey
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // Items opened before per-project routing recorded no project; they live in the default one.
        return rows
            .Where(row => (row.PlaneProjectId ?? defaultProjectId) is not null)
            .Select(row => new TrackedIntake(
                row.Id,
                row.Channel,
                row.ThreadKey,
                row.ParticipantNormalized,
                row.CustomerName,
                row.Subject,
                (row.PlaneProjectId ?? defaultProjectId)!.Value,
                row.WorkItemId,
                row.PlaneSequenceId,
                row.PlaneStatusKey))
            .ToList();
    }

    public async Task SaveAsync(
        Guid conversationId,
        string statusKey,
        bool closed,
        DateTimeOffset now,
        OutboundDraft? reply,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Conversations
                .Where(conversation => conversation.Id == conversationId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(conversation => conversation.PlaneStatusKey, statusKey)
                    .SetProperty(conversation => conversation.PlaneStatusCheckedAt, now)
                    .SetProperty(conversation => conversation.PlaneClosed, closed),
                    cancellationToken);

            if (reply is not null)
            {
                db.OutboundMessages.Add(new OutboundMessage
                {
                    Id = Guid.NewGuid(),
                    ConversationId = reply.ConversationId,
                    Channel = reply.Channel,
                    Recipient = reply.Recipient,
                    ThreadKey = reply.ThreadKey,
                    Subject = reply.Subject,
                    Body = reply.Body,
                    Reason = reply.Reason,
                    State = OutboundState.Pending,
                    CreatedAt = now
                });
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        });
    }
}

public sealed class OutboundQueue(HubDbContext db) : IOutboundQueue
{
    public async Task<IReadOnlyList<OutboundJob>> GetDueAsync(int take, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return await db.OutboundMessages
            .Where(reply => reply.State == OutboundState.Pending && (reply.NextAttemptAt == null || reply.NextAttemptAt <= now))
            .OrderBy(reply => reply.CreatedAt)
            .Take(take)
            .Select(reply => new OutboundJob(
                reply.Id,
                reply.ConversationId,
                reply.Channel,
                reply.Recipient,
                reply.ThreadKey,
                reply.Subject,
                reply.Body,
                reply.Attempts))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task MarkSentAsync(Guid id, string? externalId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var clipped = TextClip.ClipOrNull(externalId, 512);
        await db.OutboundMessages
            .Where(reply => reply.Id == id)
            .ExecuteUpdateAsync(update => update
                .SetProperty(reply => reply.State, OutboundState.Sent)
                .SetProperty(reply => reply.SentAt, now)
                .SetProperty(reply => reply.ExternalMessageId, clipped)
                .SetProperty(reply => reply.Error, (string?)null)
                .SetProperty(reply => reply.NextAttemptAt, (DateTimeOffset?)null),
                cancellationToken);
    }

    public async Task MarkFailedAsync(Guid id, string error, DateTimeOffset? retryAt, CancellationToken cancellationToken)
    {
        var state = retryAt is null ? OutboundState.Failed : OutboundState.Pending;
        await db.OutboundMessages
            .Where(reply => reply.Id == id)
            .ExecuteUpdateAsync(update => update
                .SetProperty(reply => reply.State, state)
                .SetProperty(reply => reply.Attempts, reply => reply.Attempts + 1)
                .SetProperty(reply => reply.Error, error)
                .SetProperty(reply => reply.NextAttemptAt, retryAt),
                cancellationToken);
    }
}
