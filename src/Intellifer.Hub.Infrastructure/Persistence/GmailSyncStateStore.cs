using Intellifer.Hub.Application;
using Intellifer.Hub.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Intellifer.Hub.Infrastructure.Persistence;

public sealed class GmailSyncStateStore(HubDbContext db) : IGmailSyncStateStore
{
    public async Task<string?> GetHistoryIdAsync(string mailbox, CancellationToken cancellationToken)
    {
        var state = await db.GmailSyncStates.SingleOrDefaultAsync(item => item.Mailbox == mailbox, cancellationToken);
        return state?.HistoryId;
    }

    public async Task SetHistoryIdAsync(string mailbox, string historyId, CancellationToken cancellationToken)
    {
        var state = await db.GmailSyncStates.SingleOrDefaultAsync(item => item.Mailbox == mailbox, cancellationToken);
        if (state is null)
        {
            db.GmailSyncStates.Add(new GmailSyncState
            {
                Mailbox = mailbox,
                HistoryId = historyId,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            state.HistoryId = historyId;
            state.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
