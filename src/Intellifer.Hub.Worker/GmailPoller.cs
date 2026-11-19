using Intellifer.Hub.Application;
using Microsoft.Extensions.Options;
using Intellifer.Hub.Application.Options;

namespace Intellifer.Hub.Worker;

public sealed class GmailPoller(
    IServiceScopeFactory scopes,
    IOptions<GmailOptions> options,
    ILogger<GmailPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(options.Value.PollIntervalSeconds, 15, 300));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sync = scope.ServiceProvider.GetRequiredService<GmailInboundSync>();
                await sync.RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Gmail poll failed");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}
