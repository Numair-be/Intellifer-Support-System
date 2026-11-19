using Intellifer.Hub.Application;
using Intellifer.Hub.Application.Options;
using Microsoft.Extensions.Options;

namespace Intellifer.Hub.Worker;

/// <summary>Drains the Postgres queue of bug and error messages into Plane's intake.</summary>
public sealed class PlaneIntakePoller(
    IServiceScopeFactory scopes,
    IOptions<PlaneOptions> options,
    ILogger<PlaneIntakePoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.IsConfigured)
        {
            logger.LogInformation("Plane intake is off. Set PLANE_URL, PLANE_API_KEY, PLANE_WORKSPACE_SLUG and PLANE_PROJECT_ID to turn it on.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(options.Value.PollIntervalSeconds, 5, 300));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<PlaneIntakeDispatcher>();
                await dispatcher.RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Plane intake run failed");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}
