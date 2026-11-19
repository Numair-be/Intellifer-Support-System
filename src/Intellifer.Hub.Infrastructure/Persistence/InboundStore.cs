using Intellifer.Hub.Application;
using Intellifer.Hub.Domain;
using Intellifer.Hub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Intellifer.Hub.Infrastructure.Persistence;

public sealed class InboundStore(HubDbContext db) : IInboundStore
{
    public async Task<bool> SaveAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        try
        {
            return await SaveOnceAsync(message, cancellationToken);
        }
        catch (ConversationConflictException)
        {
            return await SaveOnceAsync(message, cancellationToken);
        }
    }

    private async Task<bool> SaveOnceAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            db.ProcessedMessages.Add(new ProcessedMessage
            {
                Channel = message.Channel,
                ExternalMessageId = message.ExternalMessageId,
                ProcessedAt = now
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            var conversation = await db.Conversations.SingleOrDefaultAsync(
                item => item.Channel == message.Channel && item.ThreadKey == message.ThreadKey,
                cancellationToken);

            if (conversation is null)
            {
                conversation = new Conversation
                {
                    Id = Guid.NewGuid(),
                    Channel = message.Channel,
                    ThreadKey = message.ThreadKey,
                    ParticipantNormalized = message.SenderNormalized,
                    Subject = message.Subject,
                    CreatedAt = now,
                    LastInboundAt = message.ReceivedAt,
                    PlaneTracked = message.FlaggedAsIssue
                };
                db.Conversations.Add(conversation);
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException exception) when (IsConversationConflict(exception))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw new ConversationConflictException();
                }
            }
            else
            {
                conversation.ParticipantNormalized = message.SenderNormalized;
                if (!string.IsNullOrWhiteSpace(message.Subject))
                    conversation.Subject = message.Subject;
                if (message.ReceivedAt > conversation.LastInboundAt)
                    conversation.LastInboundAt = message.ReceivedAt;
                if (message.FlaggedAsIssue)
                    conversation.PlaneTracked = true;
            }

            db.Messages.Add(new HubMessage
            {
                Id = Guid.NewGuid(),
                Conversation = conversation,
                Direction = Direction.Inbound,
                ExternalMessageId = message.ExternalMessageId,
                SenderRaw = message.SenderRaw,
                SenderNormalized = message.SenderNormalized,
                Subject = message.Subject,
                Body = message.Body,
                RawBody = message.RawBody,
                HasMedia = message.HasMedia,
                MediaSummary = message.MediaSummary,
                CorrelationId = message.CorrelationId,
                ReceivedAt = message.ReceivedAt,
                IngestedAt = now,
                // Every message waits in the Plane queue; the dispatcher judges each burst of messages together.
                PlaneState = message.QueueForPlane || conversation.PlaneTracked ? PlaneSyncState.Pending : null,
                Attachments = message.Attachments.Select(attachment => new MessageAttachment
                {
                    Id = Guid.NewGuid(),
                    FileName = TextClip.Clip(attachment.FileName, 255),
                    ContentType = TextClip.Clip(attachment.ContentType, 128),
                    Size = attachment.Data?.LongLength ?? attachment.Size,
                    Data = attachment.SkipReason is null ? attachment.Data : null,
                    Source = attachment.SkipReason is null && attachment.Data is null ? attachment.Source : null,
                    State = attachment.SkipReason is not null ? AttachmentState.Skipped
                        : attachment.Data is not null ? AttachmentState.Ready
                        : AttachmentState.PendingDownload,
                    Error = TextClip.ClipOrNull(attachment.SkipReason, 1_000),
                    CreatedAt = now
                }).ToList()
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        });
    }

    public async Task SetCustomerAsync(
        ChannelType channel,
        string threadKey,
        CustomerMatch? match,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
    {
        int? contactId = match?.ContactId;
        var name = TextClip.ClipOrNull(match?.Name, 256);
        var company = TextClip.ClipOrNull(match?.CompanyName, 256);
        var project = match is null || match.Projects.Count == 0 ? null : TextClip.Clip(string.Join(", ", match.Projects.Select(project => project.ToString())), 256);
        await db.Conversations
            .Where(conversation => conversation.Channel == channel && conversation.ThreadKey == threadKey)
            .ExecuteUpdateAsync(update => update
                .SetProperty(conversation => conversation.CustomerContactId, contactId)
                .SetProperty(conversation => conversation.CustomerName, name)
                .SetProperty(conversation => conversation.CustomerCompany, company)
                .SetProperty(conversation => conversation.CustomerProject, project)
                .SetProperty(conversation => conversation.CustomerCheckedAt, checkedAt),
                cancellationToken);
    }

    private static bool IsConversationConflict(DbUpdateException exception)
    {
        var constraint = FindConstraintName(exception);
        return constraint is not null
            && constraint.Contains("conversation", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUniqueViolation(Exception exception) => FindConstraintName(exception) is not null;

    private static string? FindConstraintName(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres && postgres.SqlState == PostgresErrorCodes.UniqueViolation)
                return postgres.ConstraintName ?? "unique";
        }

        return null;
    }

    private sealed class ConversationConflictException : Exception;
}
