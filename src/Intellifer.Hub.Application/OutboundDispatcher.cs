using Microsoft.Extensions.Logging;

namespace Intellifer.Hub.Application;

/// <summary>Sends queued replies on their channel, retrying with backoff when a channel is down.</summary>
public sealed class OutboundDispatcher(
    IOutboundQueue queue,
    IEnumerable<IReplySender> senders,
    TimeProvider clock,
    ILogger<OutboundDispatcher> logger)
{
    private const int MaxAttempts = 10;
    private const int BatchSize = 10;

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var byChannel = senders.ToDictionary(sender => sender.Channel);
        var jobs = await queue.GetDueAsync(BatchSize, clock.GetUtcNow(), cancellationToken);
        var sent = 0;

        foreach (var job in jobs)
        {
            try
            {
                if (!byChannel.TryGetValue(job.Channel, out var sender) || !sender.IsConfigured)
                    throw new InvalidOperationException($"No {job.Channel} sender is configured.");

                var externalId = await sender.SendAsync(job, cancellationToken);
                await queue.MarkSentAsync(job.Id, externalId, clock.GetUtcNow(), cancellationToken);
                sent++;
                logger.LogInformation("Sent {Channel} reply {ReplyId} for conversation {ConversationId}", job.Channel, job.Id, job.ConversationId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var attempts = job.Attempts + 1;
                var error = TextClip.Clip(exception.Message, 1_000);
                if (attempts >= MaxAttempts)
                {
                    await queue.MarkFailedAsync(job.Id, error, retryAt: null, cancellationToken);
                    logger.LogError("Gave up sending {Channel} reply {ReplyId} after {Attempts} attempts: {Error}", job.Channel, job.Id, attempts, error);
                    continue;
                }

                // 1m, 2m, 4m ... capped at 2 hours: a WhatsApp link can be down for a while.
                var delay = TimeSpan.FromSeconds(Math.Min(60 * Math.Pow(2, attempts - 1), 7_200));
                await queue.MarkFailedAsync(job.Id, error, clock.GetUtcNow() + delay, cancellationToken);
                logger.LogWarning("Sending {Channel} reply {ReplyId} failed (attempt {Attempts}), retrying in {Delay}: {Error}", job.Channel, job.Id, attempts, delay, error);
            }
        }

        return sent;
    }
}
