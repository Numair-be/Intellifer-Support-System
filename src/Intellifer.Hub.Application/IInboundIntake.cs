using Intellifer.Hub.Domain;

namespace Intellifer.Hub.Application;

public interface IInboundIntake
{
    Task<IntakeResult> AcceptAsync(InboundMessage message, CancellationToken cancellationToken);
}
