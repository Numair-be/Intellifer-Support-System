using System.Net;
using System.Text;
using Intellifer.Hub.Application.Options;
using Intellifer.Hub.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intellifer.Hub.Application;

/// <summary>
/// Sends queued messages to Plane. The first message of a conversation opens an intake item, but only when
/// the sender is an Odoo client contact whose client project has a matching Plane project (same name or code).
/// Every later message of that conversation becomes a comment on the same work item, in the same project.
/// </summary>
public sealed class PlaneIntakeDispatcher(
    IPlaneQueue queue,
    IPlaneGateway plane,
    ICustomerDirectory customers,
    PlaneProjectResolver projects,
    IssueClassifier classifier,
    IProjectTeamDirectory team,
    IOptions<PlaneOptions> options,
    TimeProvider clock,
    ILogger<PlaneIntakeDispatcher> logger)
{
    private sealed record SenderCheck(string? Reason, CustomerMatch? Match, PlaneProject? Project, ClientProject? ClientProject = null);

    /// <summary>Which Plane project the sender's intake goes to, or why it may not open one.</summary>
    private async Task<SenderCheck> CheckSenderAsync(PlaneJob job, CancellationToken cancellationToken)
    {
        if (!customers.IsConfigured)
            return new("Odoo is not configured, so the sender's project cannot be checked.", null, null);
        if (!CustomerKey.TryCreate(job.Channel, job.Sender, out var key))
            return new($"Sender {job.Sender} has no email or phone to check in Odoo.", null, null);

        // Odoo or Plane errors throw here and are retried, so a bug report is never dropped
        // just because one of them was briefly unreachable.
        var match = await customers.FindAsync(key, cancellationToken);
        if (match is null)
            return new($"{key.Value} is not an active client contact in Odoo.", null, null);
        if (match.Projects.Count == 0)
            return new($"{match.Name} ({key.Value}) has no client project in Odoo.", match, null);

        var resolved = await projects.ResolveAsync(match.Projects, cancellationToken);
        var names = string.Join(", ", match.Projects);
        if (resolved is null)
            return new($"{match.Name} ({key.Value}) is a client contact of {names}, which has no matching Plane project.", match, null);

        var (client, target) = resolved.Value;
        if (!target.IntakeEnabled)
            return new($"Intake is off in Plane project {target.Name} ({target.Identifier}).", match, target);
        return new(null, match, target, client);
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!plane.IsConfigured)
            return 0;

        var settings = options.Value;
        var jobs = await queue.GetDueAsync(Math.Clamp(settings.BatchSize, 1, 50), clock.GetUtcNow(), cancellationToken);
        var sent = 0;

        foreach (var conversation in jobs.GroupBy(job => job.ConversationId))
        {
            var ordered = conversation.OrderBy(job => job.ReceivedAt).ToList();
            if (ordered[0].WorkItemId is null || StartsNewTicket(ordered[0], settings))
                sent += await OpenTicketAsync(ordered, settings, cancellationToken);
            else
                sent += await AddCommentsAsync(ordered, ordered[0].WorkItemId!.Value, settings, cancellationToken);
        }

        return sent;
    }

    /// <summary>
    /// The chat has a ticket, but it is closed or nothing was added to it for a long time: this burst is a new
    /// report, not a follow-up. WhatsApp chats never end, so without this every later report would be a comment.
    /// </summary>
    public static bool StartsNewTicket(PlaneJob first, PlaneOptions settings)
    {
        if (first.PlaneClosed)
            return true;
        var idle = TimeSpan.FromHours(Math.Clamp(settings.NewTicketAfterIdleHours, 1, 24 * 90));
        return first.TicketLastMessageAt is { } last && first.ReceivedAt - last >= idle;
    }

    /// <summary>
    /// A conversation without a current ticket: wait until it has gone quiet, then judge the whole burst of messages
    /// together. Bug or error wording anywhere opens one ticket holding every message; otherwise all are skipped.
    /// </summary>
    private async Task<int> OpenTicketAsync(List<PlaneJob> burst, PlaneOptions settings, CancellationToken cancellationToken)
    {
        var first = burst[0];
        var quiet = TimeSpan.FromSeconds(Math.Clamp(
            first.Channel == ChannelType.WhatsApp ? settings.WhatsAppQuietSeconds : settings.EmailQuietSeconds, 0, 3_600));
        var lastMessageAt = first.ConversationLastInboundAt ?? burst.Max(job => job.ReceivedAt);
        if (clock.GetUtcNow() - lastMessageAt < quiet)
            return 0; // still writing; look again on a later run

        try
        {
            // Judged as one text, so "it's not" + "working" across two messages still counts.
            if (!classifier.IsIssue(
                    string.Join('\n', burst.Select(job => job.Subject).Distinct()),
                    string.Join('\n', burst.Select(job => job.Body))))
            {
                await SkipAllAsync(burst, "No bug or error wording in these messages.", cancellationToken);
                return 0;
            }

            var check = await CheckSenderAsync(first, cancellationToken);
            if (check.Reason is { } reason)
            {
                await SkipAllAsync(burst, reason, cancellationToken);
                return 0;
            }

            // Use the name Odoo just returned; the stored one may be from before the contact existed.
            var match = check.Match!;
            var project = check.Project!;
            var verified = burst
                .Select(job => job with { CustomerName = match.Name, CustomerCompany = match.CompanyName ?? job.CustomerCompany })
                .ToList();
            // Title from the message that describes the problem, not from a "hi" before it.
            var headline = verified.FirstOrDefault(job => classifier.IsIssue(job.Subject, job.Body)) ?? verified[0];
            var created = await plane.CreateIntakeAsync(project.Id, Title(headline), Description(verified), cancellationToken);
            var acknowledgement = settings.AcknowledgeIntake ? Acknowledgement(headline, project, created, settings) : null;
            // Recorded only while the team lookup is connected, so connecting it later does not notify old tickets.
            var teamNotification = settings.NotifyTeam && team.IsConfigured && check.ClientProject is { } clientProject
                ? new TeamNotificationDraft(
                    first.ConversationId,
                    clientProject.Name,
                    clientProject.Code,
                    project.Name,
                    project.Id,
                    ReplyComposer.TicketNumber(project.Identifier, created.SequenceId) is { Length: > 0 } number ? number : "(new item)",
                    TextClip.Clip(TicketSummary(headline), 160),
                    match.CompanyName is null ? match.Name : $"{match.Name} ({match.CompanyName})",
                    CustomerKey.TryCreate(first.Channel, first.Sender, out var key) ? key.Value : "")
                : null;
            await queue.MarkIntakeCreatedAsync(
                verified.Select(job => job.MessageId).ToList(),
                first.ConversationId,
                project.Id,
                created,
                clock.GetUtcNow(),
                acknowledgement,
                teamNotification,
                cancellationToken);
            logger.LogInformation(
                "Opened Plane intake {Identifier}-{SequenceId} from {Count} {Channel} message(s) in conversation {ConversationId}",
                project.Identifier,
                created.SequenceId,
                verified.Count,
                first.Channel,
                first.ConversationId);
            return verified.Count;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The oldest message backs off; the rest of the conversation waits with it.
            await RecordFailureAsync(first, exception, settings, cancellationToken);
            return 0;
        }
    }

    private async Task SkipAllAsync(List<PlaneJob> burst, string reason, CancellationToken cancellationToken)
    {
        foreach (var job in burst)
            await queue.MarkSkippedAsync(job.MessageId, reason, cancellationToken);
        logger.LogInformation(
            "No Plane intake for {Count} message(s) in conversation {ConversationId}: {Reason}",
            burst.Count,
            burst[0].ConversationId,
            reason);
    }

    /// <summary>A conversation that already has a ticket: each message becomes a comment on it, in order.</summary>
    private async Task<int> AddCommentsAsync(List<PlaneJob> messages, Guid workItemId, PlaneOptions settings, CancellationToken cancellationToken)
    {
        var sent = 0;
        foreach (var job in messages)
        {
            try
            {
                // Conversations from before per-project routing have no project recorded; they used PLANE_PROJECT_ID.
                var project = job.PlaneProjectId ?? settings.DefaultProjectId
                    ?? throw new InvalidOperationException(
                        "This conversation's Plane project is unknown. Set PLANE_PROJECT_ID to the project of older intake items.");
                await plane.AddCommentAsync(project, workItemId, Comment(job), job.MessageId.ToString(), cancellationToken);
                await queue.MarkSentAsync(job.MessageId, clock.GetUtcNow(), cancellationToken);
                logger.LogInformation("Added message {MessageId} to Plane work item {WorkItemId}", job.MessageId, workItemId);
                sent++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await RecordFailureAsync(job, exception, settings, cancellationToken);
                // Keep the conversation in order: its later messages wait for this one.
                break;
            }
        }

        return sent;
    }

    /// <summary>"Thank you for contacting us ... your ticket number is PLANE-10", on the channel the customer used.</summary>
    public static OutboundDraft Acknowledgement(PlaneJob job, PlaneProject project, PlaneIntakeCreated created, PlaneOptions settings)
    {
        var ticket = ReplyComposer.TicketNumber(project.Identifier, created.SequenceId);
        var number = ticket.Length == 0
            ? ""
            : $"\n\nYour ticket number is {ticket}. Please mention it if you contact us about this request.";
        var message = "Thank you for contacting us. We have received your request and our team will review it shortly."
            + number
            + "\n\nWe will let you know here whenever its status changes.";
        return ReplyComposer.Compose(
            job.ConversationId,
            job.Channel,
            job.Sender,
            job.ThreadKey ?? job.Sender,
            job.CustomerName,
            job.Subject,
            message,
            settings.ReplySignature,
            "plane-intake:created",
            ticket.Length == 0 ? "We have received your request" : $"We have received your request ({ticket})");
    }

    private async Task RecordFailureAsync(PlaneJob job, Exception exception, PlaneOptions settings, CancellationToken cancellationToken)
    {
        var attempts = job.Attempts + 1;
        var error = TextClip.Clip(exception.Message, 1_000);
        if (attempts >= Math.Max(1, settings.MaxAttempts))
        {
            await queue.MarkFailedAsync(job.MessageId, error, retryAt: null, cancellationToken);
            logger.LogError(
                "Gave up sending message {MessageId} to Plane after {Attempts} attempts: {Error}",
                job.MessageId,
                attempts,
                error);
            return;
        }

        // 30s, 1m, 2m, 4m ... capped at an hour.
        var delay = TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, attempts - 1), 3_600));
        await queue.MarkFailedAsync(job.MessageId, error, clock.GetUtcNow() + delay, cancellationToken);
        logger.LogWarning(
            "Sending message {MessageId} to Plane failed (attempt {Attempts}), retrying in {Delay}: {Error}",
            job.MessageId,
            attempts,
            delay,
            error);
    }

    /// <summary>What the report is about, without the "[Channel] Name:" prefix of the Plane title.</summary>
    public static string TicketSummary(PlaneJob job) =>
        !string.IsNullOrWhiteSpace(job.Subject)
            ? job.Subject.Trim()
            : FirstLine(job.Body) ?? (job.HasMedia ? "Media message" : "New message");

    public static string Title(PlaneJob job)
    {
        var who = job.CustomerName ?? job.Sender;
        var summary = !string.IsNullOrWhiteSpace(job.Subject)
            ? job.Subject.Trim()
            : FirstLine(job.Body) ?? (job.HasMedia ? "Media message" : "New message");
        return TextClip.Clip($"[{job.Channel}] {who}: {summary}", 255);
    }

    public static string Description(PlaneJob job) => Description([job]);

    /// <summary>One ticket for a burst of messages: who and when once, then every message in order.</summary>
    public static string Description(IReadOnlyList<PlaneJob> messages)
    {
        var first = messages[0];
        var html = new StringBuilder();
        html.Append("<p><strong>From:</strong> ").Append(Encode(Who(first))).Append("<br>");
        html.Append("<strong>Channel:</strong> ").Append(Encode(first.Channel.ToString())).Append("<br>");
        html.Append("<strong>Received:</strong> ").Append(Encode(first.ReceivedAt.ToString("yyyy-MM-dd HH:mm 'UTC'"))).Append("</p>");
        var subject = messages.Select(job => job.Subject).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (subject is not null)
            html.Append("<p><strong>Subject:</strong> ").Append(Encode(subject)).Append("</p>");

        if (messages.Count == 1)
        {
            html.Append(Body(first));
            return html.ToString();
        }

        foreach (var job in messages)
        {
            html.Append("<p><strong>").Append(Encode(job.ReceivedAt.ToString("HH:mm:ss 'UTC'"))).Append("</strong></p>");
            html.Append(Body(job));
        }

        return html.ToString();
    }

    public static string Comment(PlaneJob job) =>
        $"<p><strong>{Encode(Who(job))}</strong> via {Encode(job.Channel.ToString())}, " +
        $"{Encode(job.ReceivedAt.ToString("yyyy-MM-dd HH:mm 'UTC'"))}</p>{Body(job)}";

    private static string Who(PlaneJob job)
    {
        if (job.CustomerName is null)
            return job.Sender;
        var company = job.CustomerCompany is null ? "" : $", {job.CustomerCompany}";
        return $"{job.CustomerName}{company} ({job.Sender})";
    }

    private static string Body(PlaneJob job)
    {
        var html = new StringBuilder();
        var text = job.Body.Trim();
        if (text.Length > 0)
        {
            // Keep the customer's paragraphs and line breaks.
            foreach (var paragraph in text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
                html.Append("<p>").Append(string.Join("<br>", paragraph.Split('\n').Select(Encode))).Append("</p>");
        }

        if (job.HasMedia)
            html.Append("<p><em>Attachments: ").Append(Encode(job.MediaSummary ?? "media")).Append("</em></p>");
        return html.Length > 0 ? html.ToString() : "<p><em>(no text)</em></p>";
    }

    private static string? FirstLine(string body) =>
        body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
