using Intellifer.Hub.Domain;

namespace Intellifer.Hub.Application;

public interface IInboundStore
{
    Task<bool> SaveAsync(InboundMessage message, CancellationToken cancellationToken);

    /// <summary>Records the customer found for a conversation. A null match means the lookup found nobody.</summary>
    Task SetCustomerAsync(
        ChannelType channel,
        string threadKey,
        CustomerMatch? match,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken);
}
