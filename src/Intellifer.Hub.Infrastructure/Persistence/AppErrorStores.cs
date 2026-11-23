using Intellifer.Hub.Application;
using Intellifer.Hub.Domain;
using Intellifer.Hub.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Intellifer.Hub.Infrastructure.Persistence;

public sealed class AppErrorStore(HubDbContext db) : IAppErrorStore
{
    public async Task<Guid?> FindPendingAsync(string projectKey, string fingerprint, CancellationToken cancellationToken) =>
        await db.AppErrors
            .Where(error => error.ProjectKey == projectKey && error.Fingerprint == fingerprint && error.PlaneState == PlaneSyncState.Pending)
            .OrderByDescending(error => error.ReceivedAt)
            .Select(error => (Guid?)error.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<Guid?> FindTicketedAsync(string projectKey, string fingerprint, DateTimeOffset since, CancellationToken cancellationToken) =>
        await db.AppErrors
            .Where(error => error.ProjectKey == projectKey
                && error.Fingerprint == fingerprint
                && error.PlaneState == PlaneSyncState.Sent
                && error.LastSeenAt >= since)
            .OrderByDescending(error => error.LastSeenAt)
            .Select(error => (Guid?)error.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<int> CountRepeatAsync(Guid errorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await db.AppErrors
            .Where(error => error.Id == errorId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(error => error.Occurrences, error => error.Occurrences + 1)
                .SetProperty(error => error.LastSeenAt, now),
                cancellationToken);
        return await db.AppErrors.Where(error => error.Id == errorId).Select(error => error.Occurrences).FirstAsync(cancellationToken);
    }

    public async Task<Guid> AddAsync(AppErrorReport report, string projectKey, string fingerprint, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var error = new AppError
        {
            Id = Guid.NewGuid(),
            Project = TextClip.Clip(report.Project.Trim(), 256),
            ProjectKey = TextClip.Clip(projectKey, 256),
            Source = TextClip.ClipOrNull(report.Source?.Trim(), 256),
            Message = TextClip.Clip(report.Message, AppErrorIntake.MaxMessageLength),
            Fingerprint = fingerprint,
            OccurredAt = report.OccurredAt ?? now,
            ReceivedAt = now,
            LastSeenAt = now
        };
        db.AppErrors.Add(error);
        await db.SaveChangesAsync(cancellationToken);
        return error.Id;
    }

    public async Task<IReadOnlyList<AppErrorJob>> GetPendingAsync(int take, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.AppErrors
            .Where(error => error.PlaneState == PlaneSyncState.Pending
                && (error.PlaneNextAttemptAt == null || error.PlaneNextAttemptAt <= now))
            .OrderBy(error => error.ReceivedAt)
            .Take(take)
            .Select(error => new AppErrorJob(
                error.Id,
                error.Project,
                error.ProjectKey,
                error.Source,
                error.Message,
                error.OccurredAt,
                error.ReceivedAt,
                error.Occurrences,
                error.PlaneAttempts))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task<int> CountTicketsSinceAsync(string projectKey, DateTimeOffset since, CancellationToken cancellationToken) =>
        await db.AppErrors
            .Where(error => error.ProjectKey == projectKey && error.PlaneState == PlaneSyncState.Sent && error.TicketedAt >= since)
            .Select(error => error.PlaneWorkItemId)
            .Distinct()
            .CountAsync(cancellationToken);

    public async Task MarkSentAsync(
        IReadOnlyList<Guid> ids,
        Guid projectId,
        PlaneIntakeCreated created,
        TeamNotificationDraft? teamNotification,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.AppErrors.Where(error => ids.Contains(error.Id))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(error => error.PlaneState, PlaneSyncState.Sent)
                    .SetProperty(error => error.PlaneProjectId, projectId)
                    .SetProperty(error => error.PlaneWorkItemId, created.WorkItemId)
                    .SetProperty(error => error.PlaneSequenceId, created.SequenceId)
                    .SetProperty(error => error.TicketedAt, now)
                    .SetProperty(error => error.PlaneError, (string?)null)
                    .SetProperty(error => error.PlaneNextAttemptAt, (DateTimeOffset?)null),
                    cancellationToken);
            if (teamNotification is not null)
            {
                db.TeamNotifications.Add(new TeamNotification
                {
                    Id = Guid.NewGuid(),
                    ConversationId = null,
                    ClientProject = TextClip.Clip(teamNotification.ClientProject, 256),
                    ClientProjectCode = TextClip.ClipOrNull(teamNotification.ClientProjectCode, 64),
                    PlaneProject = TextClip.Clip(teamNotification.PlaneProject, 256),
                    PlaneProjectId = teamNotification.PlaneProjectId,
                    TicketNumber = TextClip.Clip(teamNotification.TicketNumber, 64),
                    Title = TextClip.Clip(teamNotification.Title, 255),
                    Customer = TextClip.Clip(teamNotification.Customer, 255),
                    CustomerKey = "",
                    State = TeamNotificationState.Pending,
                    CreatedAt = now
                });
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        });
    }

    public async Task MarkSkippedAsync(IReadOnlyList<Guid> ids, string reason, CancellationToken cancellationToken)
    {
        var text = TextClip.Clip(reason, 1_000);
        await db.AppErrors.Where(error => ids.Contains(error.Id))
            .ExecuteUpdateAsync(update => update
                .SetProperty(error => error.PlaneState, PlaneSyncState.Skipped)
                .SetProperty(error => error.PlaneError, text)
                .SetProperty(error => error.PlaneNextAttemptAt, (DateTimeOffset?)null),
                cancellationToken);
    }

    public async Task MarkFailedAsync(IReadOnlyList<Guid> ids, string error, DateTimeOffset? retryAt, CancellationToken cancellationToken)
    {
        var state = retryAt is null ? PlaneSyncState.Failed : PlaneSyncState.Pending;
        await db.AppErrors.Where(item => ids.Contains(item.Id))
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.PlaneState, state)
                .SetProperty(item => item.PlaneAttempts, item => item.PlaneAttempts + 1)
                .SetProperty(item => item.PlaneError, error)
                .SetProperty(item => item.PlaneNextAttemptAt, retryAt),
                cancellationToken);
    }
}
