using Intellifer.Hub.Application;
using Intellifer.Hub.Domain;
using Intellifer.Hub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Intellifer.Hub.Infrastructure.Persistence;

public sealed class AppRegistry(HubDbContext db) : IAppRegistry
{
    public async Task<IReadOnlyList<AppInfo>> ListAsync(CancellationToken cancellationToken) =>
        await db.Apps.OrderBy(app => app.Name).Select(app => ToInfo(app)).AsNoTracking().ToListAsync(cancellationToken);

    public async Task<AppInfo?> FindByKeyHashAsync(string keyHash, CancellationToken cancellationToken) =>
        await db.Apps.Where(app => app.KeyHash == keyHash).Select(app => ToInfo(app)).AsNoTracking().FirstOrDefaultAsync(cancellationToken);

    public async Task<AppInfo?> CreateAsync(string name, string project, string keyHash, string keyHint, CancellationToken cancellationToken)
    {
        var app = new AppRegistration
        {
            Id = Guid.NewGuid(),
            Name = TextClip.Clip(name.Trim(), 100),
            Project = TextClip.Clip(project.Trim(), 256),
            KeyHash = keyHash,
            KeyHint = keyHint,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Apps.Add(app);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return null;
        }

        return ToInfo(app);
    }

    public async Task<AppInfo?> UpdateAsync(Guid id, string? project, bool? active, CancellationToken cancellationToken)
    {
        var app = await db.Apps.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (app is null)
            return null;
        if (!string.IsNullOrWhiteSpace(project))
            app.Project = TextClip.Clip(project.Trim(), 256);
        if (active is { } value)
            app.Active = value;
        await db.SaveChangesAsync(cancellationToken);
        return ToInfo(app);
    }

    public async Task<AppInfo?> ReplaceKeyAsync(Guid id, string keyHash, string keyHint, CancellationToken cancellationToken)
    {
        var app = await db.Apps.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (app is null)
            return null;
        app.KeyHash = keyHash;
        app.KeyHint = keyHint;
        await db.SaveChangesAsync(cancellationToken);
        return ToInfo(app);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Apps.Where(app => app.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;

    private static AppInfo ToInfo(AppRegistration app) =>
        new(app.Id, app.Name, app.Project, app.KeyHint, app.Active, app.CreatedAt, app.LastErrorAt);
}

public sealed class AppErrorStore(HubDbContext db) : IAppErrorStore
{
    public async Task<Guid?> FindRecentAsync(Guid appId, string fingerprint, DateTimeOffset since, CancellationToken cancellationToken) =>
        await db.AppErrors
            .Where(error => error.AppId == appId && error.Fingerprint == fingerprint && error.ReceivedAt >= since)
            .OrderByDescending(error => error.ReceivedAt)
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
        await TouchAppAsync(errorId, now, cancellationToken);
        return await db.AppErrors.Where(error => error.Id == errorId).Select(error => error.Occurrences).FirstAsync(cancellationToken);
    }

    public async Task<int> CountTicketedSinceAsync(Guid appId, DateTimeOffset since, CancellationToken cancellationToken) =>
        await db.AppErrors.CountAsync(
            error => error.AppId == appId && error.ReceivedAt >= since && error.PlaneState != PlaneSyncState.Skipped,
            cancellationToken);

    public async Task<Guid> AddAsync(
        Guid appId,
        AppErrorReport report,
        string fingerprint,
        DateTimeOffset now,
        string? skipReason,
        CancellationToken cancellationToken)
    {
        var error = new AppError
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            Message = TextClip.Clip(report.Message, AppErrorIntake.MaxMessageLength),
            Source = TextClip.ClipOrNull(report.Source?.Trim(), 256),
            Level = TextClip.ClipOrNull(report.Level?.Trim(), 32),
            Fingerprint = fingerprint,
            OccurredAt = report.OccurredAt ?? now,
            ReceivedAt = now,
            LastSeenAt = now,
            PlaneState = skipReason is null ? PlaneSyncState.Pending : PlaneSyncState.Skipped,
            PlaneError = TextClip.ClipOrNull(skipReason, 1_000)
        };
        db.AppErrors.Add(error);
        await db.SaveChangesAsync(cancellationToken);
        await db.Apps.Where(app => app.Id == appId)
            .ExecuteUpdateAsync(update => update.SetProperty(app => app.LastErrorAt, now), cancellationToken);
        return error.Id;
    }

    public async Task<IReadOnlyList<AppErrorJob>> GetDueAsync(int take, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.AppErrors
            .Where(error => error.PlaneState == PlaneSyncState.Pending
                && (error.PlaneNextAttemptAt == null || error.PlaneNextAttemptAt <= now))
            .OrderBy(error => error.ReceivedAt)
            .Take(take)
            .Select(error => new AppErrorJob(
                error.Id,
                error.App.Name,
                error.App.Project,
                error.Message,
                error.Source,
                error.Level,
                error.OccurredAt,
                error.Occurrences,
                error.PlaneAttempts))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task MarkSentAsync(Guid id, Guid projectId, PlaneIntakeCreated created, CancellationToken cancellationToken) =>
        await db.AppErrors.Where(error => error.Id == id)
            .ExecuteUpdateAsync(update => update
                .SetProperty(error => error.PlaneState, PlaneSyncState.Sent)
                .SetProperty(error => error.PlaneProjectId, projectId)
                .SetProperty(error => error.PlaneWorkItemId, created.WorkItemId)
                .SetProperty(error => error.PlaneSequenceId, created.SequenceId)
                .SetProperty(error => error.PlaneError, (string?)null)
                .SetProperty(error => error.PlaneNextAttemptAt, (DateTimeOffset?)null),
                cancellationToken);

    public async Task MarkSkippedAsync(Guid id, string reason, CancellationToken cancellationToken)
    {
        var text = TextClip.Clip(reason, 1_000);
        await db.AppErrors.Where(error => error.Id == id)
            .ExecuteUpdateAsync(update => update
                .SetProperty(error => error.PlaneState, PlaneSyncState.Skipped)
                .SetProperty(error => error.PlaneError, text)
                .SetProperty(error => error.PlaneNextAttemptAt, (DateTimeOffset?)null),
                cancellationToken);
    }

    public async Task MarkFailedAsync(Guid id, string error, DateTimeOffset? retryAt, CancellationToken cancellationToken)
    {
        var state = retryAt is null ? PlaneSyncState.Failed : PlaneSyncState.Pending;
        await db.AppErrors.Where(item => item.Id == id)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.PlaneState, state)
                .SetProperty(item => item.PlaneAttempts, item => item.PlaneAttempts + 1)
                .SetProperty(item => item.PlaneError, error)
                .SetProperty(item => item.PlaneNextAttemptAt, retryAt),
                cancellationToken);
    }

    private Task<int> TouchAppAsync(Guid errorId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Apps.Where(app => db.AppErrors.Any(error => error.Id == errorId && error.AppId == app.Id))
            .ExecuteUpdateAsync(update => update.SetProperty(app => app.LastErrorAt, now), cancellationToken);
}
