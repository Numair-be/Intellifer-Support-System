using Intellifer.Hub.Application;
using Intellifer.Hub.Application.Options;
using Microsoft.Extensions.Options;

namespace Intellifer.Hub.Worker;

/// <summary>Checks open Plane intake items for status changes and queues replies to customers.</summary>
public sealed class PlaneStatusPoller(
    IServiceScopeFactory scopes,
    IOptions<PlaneOptions> options,
    ILogger<PlaneStatusPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.IsConfigured || !options.Value.StatusReplies)
        {
            logger.LogInformation("Plane status replies are off.");
            return;
        }

        // Each item is checked every PLANE_STATUS_POLL_MINUTES; this loop just looks for due items.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PlaneStatusWatcher>().RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Plane status check failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}

/// <summary>Sends queued replies by email or WhatsApp.</summary>
public sealed class OutboundPoller(IServiceScopeFactory scopes, ILogger<OutboundPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<OutboundDispatcher>().RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Sending replies failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}

/// <summary>Fetches WhatsApp images and attaches images to their Plane tickets.</summary>
public sealed class AttachmentPoller(IServiceScopeFactory scopes, ILogger<AttachmentPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AttachmentDispatcher>().RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Carrying images to Plane failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }
}

/// <summary>Keeps the WhatsApp gateway set up: the hub's session exists and its webhook points at the hub.</summary>
public sealed class WhatsAppSetupPoller(IWhatsAppGateway gateway, ILogger<WhatsAppSetupPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!gateway.IsConfigured)
        {
            logger.LogInformation("WhatsApp gateway is not configured (OPENWA_URL, WA_API_KEY).");
            return;
        }

        string? lastStatus = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromMinutes(5);
            try
            {
                var state = await gateway.EnsureSetupAsync(stoppingToken);
                if (state.Status != lastStatus)
                    logger.LogInformation("WhatsApp session is {Status}{Hint}", state.Status,
                        state.IsReady ? "" : ". Link it with POST /api/whatsapp/link (see README).");
                lastStatus = state.Status;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // The gateway is probably still starting; try again soon.
                logger.LogWarning("WhatsApp gateway not reachable yet: {Error}", exception.Message);
                delay = TimeSpan.FromSeconds(20);
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}
