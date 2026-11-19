using Intellifer.Hub.Application.Options;
using Intellifer.Hub.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intellifer.Hub.Application;

public sealed class GmailInboundSync(
    IGmailMailbox mailbox,
    IGmailSyncStateStore state,
    IInboundIntake intake,
    ISenderNormalizer normalizer,
    IChannelHealthStore health,
    IOptions<GmailOptions> options,
    IOptions<PlaneOptions> planeOptions,
    ILogger<GmailInboundSync> logger)
{
    // Inline images smaller than this are signature logos and icons, not screenshots.
    // Files the customer attached are always kept, however small.
    private const long MinimumImageBytes = 4 * 1024;

    private async Task<IReadOnlyList<InboundAttachment>> DownloadImagesAsync(GmailFetchedMessage fetched, CancellationToken cancellationToken)
    {
        var images = new List<InboundAttachment>();
        var maxBytes = planeOptions.Value.AttachmentMaxBytes;
        foreach (var part in fetched.Images ?? [])
        {
            if (part.IsInline && part.Size is > 0 and < MinimumImageBytes)
                continue;
            if (part.Size > maxBytes)
            {
                // Recorded without the file, so the ticket still says an image was left out.
                images.Add(new InboundAttachment(part.FileName, part.MimeType, part.Size, SkipReason: TooLarge(part.Size, maxBytes)));
                continue;
            }

            try
            {
                var data = part.InlineData ?? (part.AttachmentId is null
                    ? null
                    : await mailbox.GetAttachmentAsync(fetched.Id, part.AttachmentId, cancellationToken));
                if (data is { Length: > 0 } && (!part.IsInline || data.LongLength >= MinimumImageBytes))
                    images.Add(new InboundAttachment(part.FileName, part.MimeType, data.Length, data));
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Could not download image {FileName} from email {MessageId}: {Error}", part.FileName, fetched.Id, exception.Message);
            }
        }

        return images;
    }

    public static string TooLarge(long size, long maxBytes) =>
        $"Image is {size / 1024.0 / 1024.0:0.#} MB; the limit is {maxBytes / 1024 / 1024} MB (PLANE_ATTACHMENT_MAX_MB).";

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!mailbox.IsConfigured)
        {
            logger.LogInformation("Gmail polling is waiting for OAuth settings.");
            await health.UpsertAsync(
                ChannelType.Email,
                "not-configured",
                "Set the Gmail client id, client secret, and refresh token.",
                cancellationToken);
            return;
        }

        try
        {
            var profile = await mailbox.GetProfileAsync(cancellationToken);
            var start = await state.GetHistoryIdAsync(profile.EmailAddress, cancellationToken);
            if (start is null)
            {
                await IngestRecentAsync(profile.EmailAddress, cancellationToken);
                start = profile.HistoryId;
            }

            GmailHistoryPage page;
            try
            {
                page = await mailbox.ListHistoryAsync(start, cancellationToken);
            }
            catch (GmailHistoryExpiredException)
            {
                logger.LogWarning("Gmail history id expired. Reading recent mail and resetting the cursor.");
                await IngestRecentAsync(profile.EmailAddress, cancellationToken);
                var fresh = await mailbox.GetProfileAsync(cancellationToken);
                await state.SetHistoryIdAsync(fresh.EmailAddress, fresh.HistoryId, cancellationToken);
                await health.UpsertAsync(ChannelType.Email, "ok", "Gmail history expired and was reset.", cancellationToken);
                return;
            }

            await IngestAsync(page.AddedMessageIds, profile.EmailAddress, cancellationToken);
            await state.SetHistoryIdAsync(profile.EmailAddress, ChooseNext(start, page.LatestHistoryId), cancellationToken);
            await health.UpsertAsync(ChannelType.Email, "ok", "Last poll succeeded.", cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Gmail sync failed");
            await health.UpsertAsync(ChannelType.Email, "error", "Gmail sync failed.", cancellationToken);
        }
    }

    private async Task IngestRecentAsync(string ownerEmail, CancellationToken cancellationToken)
    {
        var days = Math.Clamp(options.Value.BootstrapDays, 1, 30);
        var max = Math.Clamp(options.Value.BootstrapMaxMessages, 1, 200);
        var ids = await mailbox.ListRecentMessageIdsAsync(days, max, cancellationToken);
        await IngestAsync(ids, ownerEmail, cancellationToken);
    }

    private async Task IngestAsync(
        IReadOnlyList<string> messageIds,
        string ownerEmail,
        CancellationToken cancellationToken)
    {
        var owner = normalizer.Normalize(ChannelType.Email, ownerEmail);
        foreach (var messageId in messageIds.Distinct(StringComparer.Ordinal))
        {
            var fetched = await mailbox.GetMessageAsync(messageId, cancellationToken);
            if (fetched is null)
                continue;
            if (fetched.LabelIds.Contains("SENT") || fetched.LabelIds.Contains("DRAFT"))
                continue;

            var from = string.IsNullOrWhiteSpace(fetched.FromHeader) ? "unknown" : fetched.FromHeader;
            var sender = normalizer.Normalize(ChannelType.Email, from);
            if (!sender.Equals("unknown", StringComparison.Ordinal) && sender.Equals(owner, StringComparison.OrdinalIgnoreCase))
                continue;

            var plain = fetched.BodyWasHtml ? EmailBodyCleaner.ToPlain(fetched.Body) : fetched.Body;
            var cleaned = EmailBodyCleaner.Clean(plain);
            if (string.IsNullOrWhiteSpace(cleaned))
                cleaned = plain.Trim();

            var threadKey = string.IsNullOrWhiteSpace(fetched.ThreadId) ? fetched.Id : fetched.ThreadId;
            var images = await DownloadImagesAsync(fetched, cancellationToken);
            await intake.AcceptAsync(new InboundMessage
            {
                Channel = ChannelType.Email,
                ExternalMessageId = fetched.Id,
                ThreadKey = threadKey,
                SenderRaw = from,
                SenderNormalized = sender,
                Subject = fetched.Subject,
                Body = cleaned,
                RawBody = plain,
                HasMedia = fetched.AttachmentNames.Count > 0,
                MediaSummary = fetched.AttachmentNames.Count == 0 ? null : string.Join(", ", fetched.AttachmentNames),
                ReceivedAt = fetched.ReceivedAt,
                CorrelationId = "gmail:" + fetched.Id,
                Attachments = images
            }, cancellationToken);

            await mailbox.ApplyProcessedLabelAsync(fetched.Id, cancellationToken);
        }
    }

    private static string ChooseNext(string start, string? latest)
    {
        if (string.IsNullOrWhiteSpace(latest))
            return start;
        if (ulong.TryParse(start, out var startValue)
            && ulong.TryParse(latest, out var latestValue)
            && latestValue < startValue)
            return start;
        return latest;
    }
}
