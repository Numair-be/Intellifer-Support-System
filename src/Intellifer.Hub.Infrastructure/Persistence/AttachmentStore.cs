using Intellifer.Hub.Application;
using Intellifer.Hub.Domain;
using Intellifer.Hub.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Intellifer.Hub.Infrastructure.Persistence;

public sealed class AttachmentStore(HubDbContext db) : IAttachmentStore
{
    public async Task<IReadOnlyList<AttachmentDownload>> GetDownloadsDueAsync(int take, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return await db.Attachments
            .Where(attachment => attachment.State == AttachmentState.PendingDownload
                && attachment.Source != null
                && (attachment.NextAttemptAt == null || attachment.NextAttemptAt <= now)
                // Only fetch what reached a Plane ticket; other photos are never downloaded.
                && attachment.Message.PlaneState == PlaneSyncState.Sent)
            .OrderBy(attachment => attachment.CreatedAt)
            .Take(take)
            .Select(attachment => new AttachmentDownload(
                attachment.Id,
                attachment.Message.Conversation.Channel,
                attachment.Source!,
                attachment.ContentType,
                attachment.Attempts))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task SaveDownloadedAsync(Guid id, byte[] data, string contentType, CancellationToken cancellationToken)
    {
        await db.Attachments
            .Where(attachment => attachment.Id == id)
            .ExecuteUpdateAsync(update => update
                .SetProperty(attachment => attachment.Data, data)
                .SetProperty(attachment => attachment.Size, data.LongLength)
                .SetProperty(attachment => attachment.ContentType, contentType)
                .SetProperty(attachment => attachment.Source, (string?)null)
                .SetProperty(attachment => attachment.State, AttachmentState.Ready)
                .SetProperty(attachment => attachment.Attempts, 0)
                .SetProperty(attachment => attachment.Error, (string?)null)
                .SetProperty(attachment => attachment.NextAttemptAt, (DateTimeOffset?)null),
                cancellationToken);
    }

    public async Task<IReadOnlyList<AttachmentUpload>> GetUploadsDueAsync(
        int take,
        DateTimeOffset now,
        Guid? defaultProjectId,
        CancellationToken cancellationToken)
    {
        var rows = await db.Attachments
            .Where(attachment => attachment.State == AttachmentState.Ready
                && attachment.Data != null
                && (attachment.NextAttemptAt == null || attachment.NextAttemptAt <= now)
                && attachment.Message.PlaneState == PlaneSyncState.Sent
                && attachment.Message.Conversation.PlaneWorkItemId != null)
            .OrderBy(attachment => attachment.CreatedAt)
            .Take(take)
            .Select(attachment => new
            {
                attachment.Id,
                attachment.FileName,
                attachment.ContentType,
                Data = attachment.Data!,
                ProjectId = attachment.Message.Conversation.PlaneProjectId,
                WorkItemId = attachment.Message.Conversation.PlaneWorkItemId!.Value,
                attachment.Attempts
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // Tickets opened before per-project routing live in the default project.
        return rows
            .Where(row => (row.ProjectId ?? defaultProjectId) is not null)
            .Select(row => new AttachmentUpload(
                row.Id,
                row.FileName,
                row.ContentType,
                row.Data,
                (row.ProjectId ?? defaultProjectId)!.Value,
                row.WorkItemId,
                row.Attempts))
            .ToList();
    }

    public async Task MarkUploadedAsync(Guid id, Guid planeAttachmentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await db.Attachments
            .Where(attachment => attachment.Id == id)
            .ExecuteUpdateAsync(update => update
                .SetProperty(attachment => attachment.State, AttachmentState.Uploaded)
                .SetProperty(attachment => attachment.PlaneAttachmentId, planeAttachmentId)
                .SetProperty(attachment => attachment.UploadedAt, now)
                .SetProperty(attachment => attachment.Data, (byte[]?)null)
                .SetProperty(attachment => attachment.Error, (string?)null)
                .SetProperty(attachment => attachment.NextAttemptAt, (DateTimeOffset?)null),
                cancellationToken);
    }

    public async Task MarkFailedAsync(Guid id, string error, DateTimeOffset? retryAt, CancellationToken cancellationToken)
    {
        var giveUp = retryAt is null;
        await db.Attachments
            .Where(attachment => attachment.Id == id)
            .ExecuteUpdateAsync(update => update
                // Keep the current state while retrying, so a download stays a download.
                .SetProperty(attachment => attachment.State, attachment => giveUp ? AttachmentState.Failed : attachment.State)
                .SetProperty(attachment => attachment.Attempts, attachment => attachment.Attempts + 1)
                .SetProperty(attachment => attachment.Error, error)
                .SetProperty(attachment => attachment.NextAttemptAt, retryAt),
                cancellationToken);
    }

    public async Task MarkSkippedAsync(Guid id, string reason, CancellationToken cancellationToken)
    {
        await db.Attachments
            .Where(attachment => attachment.Id == id)
            .ExecuteUpdateAsync(update => update
                .SetProperty(attachment => attachment.State, AttachmentState.Skipped)
                .SetProperty(attachment => attachment.Error, reason)
                .SetProperty(attachment => attachment.Data, (byte[]?)null)
                .SetProperty(attachment => attachment.Source, (string?)null)
                .SetProperty(attachment => attachment.NextAttemptAt, (DateTimeOffset?)null),
                cancellationToken);
    }
}
