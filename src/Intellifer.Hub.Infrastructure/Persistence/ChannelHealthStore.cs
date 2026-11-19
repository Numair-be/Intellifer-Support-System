using Intellifer.Hub.Application;
using Intellifer.Hub.Domain;
using Intellifer.Hub.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Intellifer.Hub.Infrastructure.Persistence;

public sealed class ChannelHealthStore(HubDbContext db) : IChannelHealthStore
{
    public async Task UpsertAsync(ChannelType channel, string status, string? detail, CancellationToken cancellationToken)
    {
        var record = await db.ChannelHealth.SingleOrDefaultAsync(item => item.Channel == channel, cancellationToken);
        if (record is null)
        {
            db.ChannelHealth.Add(new ChannelHealthRecord
            {
                Channel = channel,
                Status = TextClip.Clip(status, 64),
                Detail = TextClip.ClipOrNull(detail, 500),
                CheckedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            record.Status = TextClip.Clip(status, 64);
            record.Detail = TextClip.ClipOrNull(detail, 500);
            record.CheckedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ChannelHealthSnapshot?> GetAsync(ChannelType channel, CancellationToken cancellationToken)
    {
        var record = await db.ChannelHealth.SingleOrDefaultAsync(item => item.Channel == channel, cancellationToken);
        return record is null ? null : ToSnapshot(record);
    }

    public async Task<IReadOnlyList<ChannelHealthSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        var records = await db.ChannelHealth.OrderBy(item => item.Channel).ToListAsync(cancellationToken);
        return records.Select(ToSnapshot).ToArray();
    }

    private static ChannelHealthSnapshot ToSnapshot(ChannelHealthRecord record) =>
        new(record.Channel, record.Status, record.Detail, record.CheckedAt);
}
