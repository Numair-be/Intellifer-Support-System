using Intellifer.Hub.Application.Options;
using Intellifer.Hub.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intellifer.Hub.Application;

/// <summary>
/// Carries images to Plane: fetches WhatsApp media, then attaches every image to the ticket of its
/// conversation once its message has reached Plane (as the intake item or a comment).
/// </summary>
public sealed class AttachmentDispatcher(
    IAttachmentStore store,
    IPlaneGateway plane,
    IWhatsAppMedia whatsApp,
    IOptions<PlaneOptions> options,
    TimeProvider clock,
    ILogger<AttachmentDispatcher> logger)
{
    private const int MaxAttempts = 8;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!plane.IsConfigured)
            return;
        await DownloadAsync(cancellationToken);
        await UploadAsync(cancellationToken);
    }

    private async Task DownloadAsync(CancellationToken cancellationToken)
    {
        var maxBytes = options.Value.AttachmentMaxBytes;
        foreach (var job in await store.GetDownloadsDueAsync(5, clock.GetUtcNow(), cancellationToken))
        {
            try
            {
                if (job.Channel != ChannelType.WhatsApp || !whatsApp.IsConfigured)
                    throw new InvalidOperationException($"No way to download {job.Channel} media is configured.");

                var (data, contentType) = await whatsApp.DownloadAsync(job.Source, cancellationToken);
                if (data.LongLength > maxBytes)
                {
                    await store.MarkSkippedAsync(job.Id, GmailInboundSync.TooLarge(data.LongLength, maxBytes), cancellationToken);
                    continue;
                }

                await store.SaveDownloadedAsync(job.Id, data, InboundAttachment.IsImage(contentType) ? contentType : job.ContentType, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await FailAsync(job.Id, job.Attempts, "download", exception, cancellationToken);
            }
        }
    }

    private async Task UploadAsync(CancellationToken cancellationToken)
    {
        // Each upload is three Plane requests; keep well inside the 60 a minute.
        var uploads = await store.GetUploadsDueAsync(5, clock.GetUtcNow(), options.Value.DefaultProjectId, cancellationToken);
        foreach (var job in uploads)
        {
            try
            {
                var id = await plane.UploadAttachmentAsync(job.ProjectId, job.WorkItemId, job.FileName, job.ContentType, job.Data, cancellationToken);
                await store.MarkUploadedAsync(job.Id, id, clock.GetUtcNow(), cancellationToken);
                logger.LogInformation("Attached {FileName} to Plane work item {WorkItemId}", job.FileName, job.WorkItemId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await FailAsync(job.Id, job.Attempts, "upload", exception, cancellationToken);
            }
        }
    }

    private async Task FailAsync(Guid id, int previousAttempts, string step, Exception exception, CancellationToken cancellationToken)
    {
        var attempts = previousAttempts + 1;
        var error = TextClip.Clip($"{step}: {exception.Message}", 1_000);
        if (attempts >= MaxAttempts)
        {
            await store.MarkFailedAsync(id, error, retryAt: null, cancellationToken);
            logger.LogError("Gave up on image {AttachmentId} after {Attempts} attempts: {Error}", id, attempts, error);
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, attempts - 1), 3_600));
        await store.MarkFailedAsync(id, error, clock.GetUtcNow() + delay, cancellationToken);
        logger.LogWarning("Image {AttachmentId} {Step} failed (attempt {Attempts}), retrying in {Delay}: {Error}", id, step, attempts, delay, error);
    }
}
