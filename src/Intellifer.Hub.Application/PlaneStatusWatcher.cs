using Intellifer.Hub.Application.Options;
using Intellifer.Hub.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intellifer.Hub.Application;

/// <summary>
/// Polls open Plane intake items. When one changes status (accepted, backlog, to do, in progress, done,
/// cancelled, declined, duplicate), it queues a reply to the customer on the channel that opened it.
/// The first check of an item only records its status, so existing items do not trigger old news.
/// </summary>
public sealed class PlaneStatusWatcher(
    IPlaneStatusStore store,
    IPlaneGateway plane,
    PlaneProjectResolver projects,
    IOptions<PlaneOptions> options,
    TimeProvider clock,
    ILogger<PlaneStatusWatcher> logger)
{
    // Plane allows 60 requests a minute; leave room for the intake queue.
    private const int ItemsPerRun = 20;

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!plane.IsConfigured || !settings.StatusReplies)
            return 0;

        var now = clock.GetUtcNow();
        var interval = TimeSpan.FromMinutes(Math.Clamp(settings.StatusPollMinutes, 1, 120));
        var items = await store.GetDueAsync(ItemsPerRun, now - interval, settings.DefaultProjectId, cancellationToken);
        var notified = 0;

        foreach (var item in items)
        {
            try
            {
                var status = await plane.GetIntakeStatusAsync(item.ProjectId, item.WorkItemId, cancellationToken);
                OutboundDraft? reply = null;
                if (item.StatusKey is not null && PlaneStatusNotice.Describe(item.StatusKey, status) is { } change)
                {
                    reply = await DraftAsync(item, change, status, settings, cancellationToken);
                    notified++;
                }

                await store.SaveAsync(item.ConversationId, status.Key, status.IsClosed, clock.GetUtcNow(), reply, cancellationToken);
                if (reply is not null)
                    logger.LogInformation(
                        "Plane item {WorkItemId} is now {Status}; queued a {Channel} reply",
                        item.WorkItemId,
                        status.Key,
                        item.Channel);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Try again next run; one bad item must not stop the others.
                logger.LogWarning("Checking Plane item {WorkItemId} failed: {Error}", item.WorkItemId, exception.Message);
            }
        }

        return notified;
    }

    private async Task<OutboundDraft> DraftAsync(
        TrackedIntake item,
        string change,
        PlaneItemStatus status,
        PlaneOptions settings,
        CancellationToken cancellationToken)
    {
        var reference = await ReferenceAsync(item, cancellationToken);
        return ReplyComposer.Compose(
            item.ConversationId,
            item.Channel,
            item.Participant,
            item.ThreadKey,
            item.CustomerName,
            item.Subject,
            $"An update on your request {reference}: it {change}",
            settings.ReplySignature,
            $"plane-status:{status.Key}",
            $"Update on your request {reference}");
    }

    private async Task<string> ReferenceAsync(TrackedIntake item, CancellationToken cancellationToken)
    {
        if (item.SequenceId is null)
            return "(your support request)";
        string? identifier = null;
        try
        {
            identifier = (await projects.GetProjectsAsync(cancellationToken)).FirstOrDefault(p => p.Id == item.ProjectId)?.Identifier;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Could not load Plane projects for the reference: {Error}", exception.Message);
        }

        return ReplyComposer.TicketNumber(identifier, item.SequenceId);
    }
}
