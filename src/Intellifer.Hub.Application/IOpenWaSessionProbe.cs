namespace Intellifer.Hub.Application;

public sealed record OpenWaSessionStatus(
    bool Configured,
    bool Reachable,
    bool? Connected,
    bool? Ready,
    string Detail);

public interface IOpenWaSessionProbe
{
    Task<OpenWaSessionStatus> ProbeAsync(CancellationToken cancellationToken);
}
