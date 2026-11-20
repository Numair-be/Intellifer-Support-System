using Intellifer.Hub.Application.Options;
using Intellifer.Hub.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intellifer.Hub.Application;

/// <summary>
/// Tells the employees on a client project that a new intake item was created for it: looks the team up,
/// then queues one message per member on WhatsApp and/or email (sent by the outbound queue, with retries).
/// </summary>
public sealed class TeamNotifier(
    ITeamNotificationStore store,
    IProjectTeamDirectory team,
    ISenderNormalizer normalizer,
    IOptions<PlaneOptions> options,
    TimeProvider clock,
    ILogger<TeamNotifier> logger)
{
    private const int MaxAttempts = 8;

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!team.IsConfigured)
            return 0;

        var queued = 0;
        foreach (var job in await store.GetDueAsync(10, clock.GetUtcNow(), cancellationToken))
        {
            try
            {
                var names = new[] { job.PlaneProject, job.ClientProject, job.ClientProjectCode }
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .ToList();
                var members = await team.GetTeamAsync(names, cancellationToken);
                var messages = members
                    .Where(member => !IsCustomer(member, job))
                    .SelectMany(member => Compose(job, member))
                    .ToList();
                var note = messages.Count == 0
                    ? $"No team members with a phone or email found for {job.PlaneProject}."
                    : null;
                await store.CompleteAsync(job.Id, messages, note, clock.GetUtcNow(), cancellationToken);
                queued += messages.Count;
                logger.LogInformation(
                    "Team of {Project} told about {Ticket}: {Count} message(s){Note}",
                    job.ClientProject,
                    job.TicketNumber,
                    messages.Count,
                    note is null ? "" : $" ({note})");
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var attempts = job.Attempts + 1;
                var error = TextClip.Clip(exception.Message, 1_000);
                DateTimeOffset? retryAt = attempts >= MaxAttempts
                    ? null
                    : clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, attempts - 1), 3_600));
                await store.MarkFailedAsync(job.Id, error, retryAt, cancellationToken);
                logger.LogWarning(
                    "Looking up the team of {Project} for {Ticket} failed (attempt {Attempts}){Final}: {Error}",
                    job.ClientProject,
                    job.TicketNumber,
                    attempts,
                    retryAt is null ? ", giving up" : "",
                    error);
            }
        }

        return queued;
    }

    /// <summary>One or two messages for a member, depending on PLANE_TEAM_CHANNELS and what contact details they have.</summary>
    public IEnumerable<OutboundDraft> Compose(TeamNotificationJob job, TeamMember member)
    {
        var settings = options.Value;
        var chatId = WhatsAppChatId(member.Phone);
        var email = string.IsNullOrWhiteSpace(member.Email) ? null : member.Email.Trim();
        var channels = (settings.TeamChannels ?? "whatsapp").ToLowerInvariant();

        var useWhatsApp = chatId is not null && (channels.Contains("whatsapp") || channels.Contains("both") || email is null);
        var useEmail = email is not null && (channels.Contains("email") || channels.Contains("both") || chatId is null);

        var link = IntakeLink(settings, job.PlaneProjectId);
        var signature = string.IsNullOrWhiteSpace(settings.ReplySignature) ? "Support" : settings.ReplySignature.Trim();
        var headline = $"New intake for project {job.PlaneProject}: {job.TicketNumber} – {job.Title}";
        var reason = $"team-notify:{job.TicketNumber}";

        if (useWhatsApp)
        {
            var text = $"{headline}\nFrom: {job.Customer}\n\nKindly update it as soon as possible."
                + (link is null ? "" : $"\n{link}")
                + $"\n\n— {signature}";
            yield return new OutboundDraft(job.ConversationId, ChannelType.WhatsApp, chatId!, chatId!, null, text, reason);
        }

        if (useEmail)
        {
            var body = $"Hello {member.Name},\n\n{headline}\nFrom: {job.Customer}\n\nKindly update it as soon as possible."
                + (link is null ? "" : $"\n\nOpen it in Plane: {link}")
                + $"\n\nRegards,\n{signature}";
            // No thread: a new email from the support mailbox.
            yield return new OutboundDraft(
                job.ConversationId,
                ChannelType.Email,
                email!,
                "",
                TextClip.Clip($"New intake {job.TicketNumber} for {job.PlaneProject}: {job.Title}", 200),
                body,
                reason);
        }
    }

    private bool IsCustomer(TeamMember member, TeamNotificationJob job)
    {
        if (string.IsNullOrWhiteSpace(job.CustomerKey))
            return false;
        if (member.Email is not null && string.Equals(member.Email.Trim(), job.CustomerKey, StringComparison.OrdinalIgnoreCase))
            return true;
        return member.Phone is not null && normalizer.Normalize(ChannelType.WhatsApp, member.Phone) == job.CustomerKey;
    }

    /// <summary>WhatsApp chat id for a phone number in any format: +91 98765 43210 -> 919876543210@c.us.</summary>
    private string? WhatsAppChatId(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return null;
        var e164 = normalizer.Normalize(ChannelType.WhatsApp, phone);
        var digits = new string(e164.Where(char.IsDigit).ToArray());
        return e164.StartsWith('+') && digits.Length >= 8 ? digits + "@c.us" : null;
    }

    private static string? IntakeLink(PlaneOptions settings, Guid projectId) =>
        string.IsNullOrWhiteSpace(settings.Url) || string.IsNullOrWhiteSpace(settings.WorkspaceSlug)
            ? null
            : $"{settings.Url.TrimEnd('/')}/{settings.WorkspaceSlug.Trim()}/projects/{projectId}/intake";
}
