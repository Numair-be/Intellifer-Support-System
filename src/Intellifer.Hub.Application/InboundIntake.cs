using Intellifer.Hub.Application.Options;
using Intellifer.Hub.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intellifer.Hub.Application;

public sealed class InboundIntake(
    IInboundStore store,
    ISenderNormalizer normalizer,
    ICustomerDirectory customers,
    IssueClassifier classifier,
    IOptions<PlaneOptions> planeOptions,
    ILogger<InboundIntake> logger) : IInboundIntake
{
    public async Task<IntakeResult> AcceptAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.ExternalMessageId))
            throw new ArgumentException("An external message id is required.", nameof(message));
        if (string.IsNullOrWhiteSpace(message.ThreadKey))
            throw new ArgumentException("A thread key is required.", nameof(message));

        var normalized = message with
        {
            ExternalMessageId = message.ExternalMessageId.Trim(),
            ThreadKey = message.ThreadKey.Trim(),
            SenderRaw = message.SenderRaw.Trim(),
            SenderNormalized = normalizer.Normalize(message.Channel, message.SenderRaw),
            Subject = TextClip.ClipOrNull(message.Subject?.Trim(), 500),
            Body = TextClip.Clip(string.IsNullOrWhiteSpace(message.Body) ? "" : message.Body.Trim(), 50_000),
            RawBody = TextClip.ClipOrNull(message.RawBody, 100_000),
            MediaSummary = TextClip.ClipOrNull(message.MediaSummary, 2_000),
            CorrelationId = TextClip.ClipOrNull(message.CorrelationId, 64),
            ReceivedAt = message.ReceivedAt == default ? DateTimeOffset.UtcNow : message.ReceivedAt,
            // Only queue for Plane when it is set up, so nothing piles up while it is not.
            FlaggedAsIssue = planeOptions.Value.IsConfigured && classifier.IsIssue(message.Subject, message.Body),
            QueueForPlane = planeOptions.Value.IsConfigured
        };

        var saved = await store.SaveAsync(normalized, cancellationToken);
        if (saved)
        {
            logger.LogInformation(
                "Stored inbound {Channel} message {ExternalMessageId}",
                normalized.Channel,
                normalized.ExternalMessageId);
            await LinkCustomerAsync(normalized, cancellationToken);
            return IntakeResult.Stored;
        }

        logger.LogInformation(
            "Ignored duplicate inbound {Channel} message {ExternalMessageId}",
            normalized.Channel,
            normalized.ExternalMessageId);
        return IntakeResult.Duplicate;
    }

    // The message is already stored. A failed lookup must never lose it, so errors are logged, not thrown.
    private async Task LinkCustomerAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        if (!customers.IsConfigured)
            return;
        if (!CustomerKey.TryCreate(message.Channel, message.SenderNormalized, out var key))
        {
            logger.LogDebug("Sender {Sender} has no email or phone to look up", message.SenderNormalized);
            return;
        }

        try
        {
            var match = await customers.FindAsync(key, cancellationToken);
            await store.SetCustomerAsync(message.Channel, message.ThreadKey, match, DateTimeOffset.UtcNow, cancellationToken);
            if (match is null)
                logger.LogInformation("No Odoo client contact has {Kind} {Sender}", key.Kind, key.Value);
            else
                logger.LogInformation(
                    "Matched {Sender} to Odoo client contact {ContactId} {CustomerName}, project {Project}",
                    key.Value,
                    match.ContactId,
                    match.Name,
                    string.Join(", ", match.Projects));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Odoo customer lookup for {Sender} failed: {Error}", key.Value, exception.Message);
        }
    }
}
