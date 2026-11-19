using Intellifer.Hub.Domain;

namespace Intellifer.Hub.Application;

public sealed record ChannelHealthSnapshot(
    ChannelType Channel,
    string Status,
    string? Detail,
    DateTimeOffset CheckedAt);

public interface IChannelHealthStore
{
    Task UpsertAsync(ChannelType channel, string status, string? detail, CancellationToken cancellationToken);

    Task<ChannelHealthSnapshot?> GetAsync(ChannelType channel, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChannelHealthSnapshot>> ListAsync(CancellationToken cancellationToken);
}
