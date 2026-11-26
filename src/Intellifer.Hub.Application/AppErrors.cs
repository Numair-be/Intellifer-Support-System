using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Intellifer.Hub.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intellifer.Hub.Application;

/// <summary>One error as an app reports it. <see cref="Project"/> is the Plane (and Odoo) project it belongs to.</summary>
public sealed record AppErrorReport(string Project, string? Source, string Message, DateTimeOffset? OccurredAt);

public sealed record AppErrorAccepted(Guid ErrorId, bool Duplicate, int Occurrences, string Status);

/// <summary>A stored error still waiting for its ticket.</summary>
public sealed record AppErrorJob(
    Guid Id,
    string Project,
    string ProjectKey,
    string? Source,
    string Message,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt,
    int Occurrences,
    int Attempts);

public interface IAppErrorStore
{
    /// <summary>The same error of this project still waiting for its ticket (inside the batch window).</summary>
    Task<Guid?> FindPendingAsync(string projectKey, string fingerprint, CancellationToken cancellationToken);

    /// <summary>The same error of this project already ticketed and last seen since <paramref name="since"/>.</summary>
    Task<Guid?> FindTicketedAsync(string projectKey, string fingerprint, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Counts one more occurrence of a stored error; returns the new count.</summary>
    Task<int> CountRepeatAsync(Guid errorId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<Guid> AddAsync(AppErrorReport report, string projectKey, string fingerprint, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Errors waiting for a ticket whose retry time (if any) has come, oldest first.</summary>
    Task<IReadOnlyList<AppErrorJob>> GetPendingAsync(int take, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Tickets opened for this project since <paramref name="since"/>.</summary>
    Task<int> CountTicketsSinceAsync(string projectKey, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Marks the errors ticketed and, in the same transaction, records the team notification when there is one.</summary>
    Task MarkSentAsync(
        IReadOnlyList<Guid> ids,
        Guid projectId,
        PlaneIntakeCreated created,
        TeamNotificationDraft? teamNotification,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task MarkSkippedAsync(IReadOnlyList<Guid> ids, string reason, CancellationToken cancellationToken);

    Task MarkFailedAsync(IReadOnlyList<Guid> ids, string error, DateTimeOffset? retryAt, CancellationToken cancellationToken);
}

/// <summary>
/// Receives an error from an app. The same error (same project, place and message) is counted instead of stored
/// twice: while its ticket is still being collected, and for APP_ERROR_REPEAT_MINUTES after it was ticketed.
/// </summary>
public sealed partial class AppErrorIntake(IAppErrorStore store, IOptions<HubOptions> options, TimeProvider clock)
{
    public const int MaxMessageLength = 100_000;

    public async Task<AppErrorAccepted> AcceptAsync(AppErrorReport report, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow();
        var projectKey = ProjectKey(report.Project);
        var fingerprint = Fingerprint(report.Source, report.Message);

        if (await store.FindPendingAsync(projectKey, fingerprint, cancellationToken) is { } pending)
        {
            var count = await store.CountRepeatAsync(pending, now, cancellationToken);
            return new AppErrorAccepted(pending, true, count, "duplicate: counted in the ticket being prepared");
        }

        var repeatWindow = TimeSpan.FromMinutes(Math.Clamp(settings.AppErrorRepeatMinutes, 0, 24 * 60));
        if (repeatWindow > TimeSpan.Zero
            && await store.FindTicketedAsync(projectKey, fingerprint, now - repeatWindow, cancellationToken) is { } ticketed)
        {
            var count = await store.CountRepeatAsync(ticketed, now, cancellationToken);
            return new AppErrorAccepted(ticketed, true, count, "repeat of an error already ticketed: counted, no new ticket");
        }

        var id = await store.AddAsync(report, projectKey, fingerprint, now, cancellationToken);
        return new AppErrorAccepted(id, false, 1, $"queued: one ticket per project for the reports of the next {BatchSeconds(settings)} s");
    }

    public static int BatchSeconds(HubOptions settings) => Math.Clamp(settings.AppErrorBatchSeconds, 0, 3_600);

    /// <summary>Projects are matched ignoring case, spaces and punctuation, like Plane project names.</summary>
    public static string ProjectKey(string project) => PlaneProjectResolver.Normalize(project);

    /// <summary>Same error = same place and text once numbers, hex ids and spacing are ignored (timestamps, ids, ports vary).</summary>
    public static string Fingerprint(string? source, string message)
    {
        var text = (source ?? "") + "\n" + (message.Length > 4_000 ? message[..4_000] : message);
        text = HexOrNumber().Replace(text.ToLowerInvariant(), "#");
        text = Whitespace().Replace(text, " ").Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    [GeneratedRegex(@"\b(0x)?[0-9a-f]*\d[0-9a-f]*\b")]
    private static partial Regex HexOrNumber();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>
/// Turns queued app errors into Plane intake items: one ticket per project for all reports that arrived within
/// APP_ERROR_BATCH_SECONDS of its first one; duplicates are already counted. The project's team is then told.
/// </summary>
public sealed class AppErrorDispatcher(
    IAppErrorStore store,
    IPlaneGateway plane,
    PlaneProjectResolver projects,
    IProjectTeamDirectory team,
    IOptions<HubOptions> hubOptions,
    IOptions<PlaneOptions> planeOptions,
    TimeProvider clock,
    ILogger<AppErrorDispatcher> logger)
{
    private const int MaxAttempts = 8;

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!plane.IsConfigured)
            return 0;

        var settings = hubOptions.Value;
        var now = clock.GetUtcNow();
        var window = TimeSpan.FromSeconds(AppErrorIntake.BatchSeconds(settings));
        var created = 0;
        var batches = (await store.GetPendingAsync(500, now, cancellationToken))
            .GroupBy(job => job.ProjectKey)
            // Still collecting: the first report of this project came in less than the batch window ago.
            .Where(group => group.Min(job => job.ReceivedAt) <= now - window)
            .Take(10)
            .ToList();

        foreach (var batch in batches)
        {
            var errors = batch.OrderBy(job => job.ReceivedAt).ToList();
            var ids = errors.Select(job => job.Id).ToList();
            var projectName = errors[0].Project.Trim();
            try
            {
                var resolved = await projects.ResolveAsync([new ClientProject(projectName, projectName)], cancellationToken);
                if (resolved is null)
                {
                    await store.MarkSkippedAsync(ids, $"No Plane project matches '{projectName}'.", cancellationToken);
                    logger.LogWarning("App errors for {Project} skipped: no matching Plane project", projectName);
                    continue;
                }

                var target = resolved.Value.Plane;
                if (!target.IntakeEnabled)
                {
                    await store.MarkSkippedAsync(ids, $"Intake is off in Plane project {target.Name} ({target.Identifier}).", cancellationToken);
                    continue;
                }

                var limit = Math.Max(1, settings.AppErrorMaxTicketsPerHour);
                var recent = await store.CountTicketsSinceAsync(batch.Key, now - TimeSpan.FromHours(1), cancellationToken);
                if (recent >= limit)
                {
                    await store.MarkSkippedAsync(ids, $"{projectName} already has {recent} error tickets in the last hour (limit {limit}).", cancellationToken);
                    logger.LogWarning("Error storm for {Project}: {Count} report(s) stored without a ticket", projectName, ids.Count);
                    continue;
                }

                var title = Title(errors);
                var ticket = await plane.CreateIntakeAsync(target.Id, title, Description(errors, target), cancellationToken);
                // The employees assigned to the project (in Odoo) are told, like for customer tickets.
                var teamNotification = planeOptions.Value.NotifyTeam && team.IsConfigured
                    ? new TeamNotificationDraft(
                        null,
                        projectName,
                        null,
                        target.Name,
                        target.Id,
                        ReplyComposer.TicketNumber(target.Identifier, ticket.SequenceId) is { Length: > 0 } number ? number : "(new item)",
                        TextClip.Clip(title, 160),
                        $"app errors of {projectName}",
                        "")
                    : null;
                await store.MarkSentAsync(ids, target.Id, ticket, teamNotification, clock.GetUtcNow(), cancellationToken);
                created++;
                logger.LogInformation(
                    "Opened Plane intake {Identifier}-{Sequence} for {Count} app error(s) of {Project}",
                    target.Identifier,
                    ticket.SequenceId,
                    errors.Count,
                    projectName);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var attempts = errors.Max(job => job.Attempts) + 1;
                var error = TextClip.Clip(exception.Message, 1_000);
                DateTimeOffset? retryAt = attempts >= MaxAttempts
                    ? null
                    : clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, attempts - 1), 3_600));
                await store.MarkFailedAsync(ids, error, retryAt, cancellationToken);
                logger.LogWarning("Sending the errors of {Project} to Plane failed (attempt {Attempts}): {Error}", projectName, attempts, error);
            }
        }

        return created;
    }

    /// <summary>
    /// One error: "[Error] KSB / DataParserController.ExtractTables – 2026-11-23 13:15 IST".
    /// Several: "[Error] KSB – 3 different errors – 2026-11-23 13:15 IST".
    /// </summary>
    public string Title(IReadOnlyList<AppErrorJob> errors)
    {
        var first = errors[0];
        var project = first.Project.Trim();
        var when = Local(errors.Min(job => job.OccurredAt));
        if (errors.Count == 1)
        {
            var where = string.IsNullOrWhiteSpace(first.Source) ? project : $"{project} / {first.Source.Trim()}";
            return TextClip.Clip($"[Error] {where} – {when}", 255);
        }

        return TextClip.Clip($"[Error] {project} – {errors.Count} different errors – {when}", 255);
    }

    public string Description(IReadOnlyList<AppErrorJob> errors, PlaneProject project)
    {
        var reports = errors.Sum(job => job.Occurrences);
        var html = new StringBuilder();
        html.Append("<p><strong>Project:</strong> ").Append(Encode($"{project.Name} ({project.Identifier})")).Append("<br>");
        html.Append("<strong>Reported as:</strong> ").Append(Encode(errors[0].Project.Trim())).Append("<br>");
        html.Append("<strong>Errors:</strong> ").Append(errors.Count).Append(errors.Count == 1 ? " error" : " different errors")
            .Append(", ").Append(reports).Append(reports == 1 ? " report" : " reports").Append("<br>");
        html.Append("<strong>Occurred:</strong> ").Append(Encode(Local(errors.Min(job => job.OccurredAt))));
        if (errors.Count > 1)
            html.Append(" to ").Append(Encode(Local(errors.Max(job => job.OccurredAt))));
        html.Append("</p>");

        for (var index = 0; index < errors.Count; index++)
        {
            var error = errors[index];
            if (errors.Count > 1)
                html.Append("<h3>").Append(index + 1).Append(". ").Append(Encode(string.IsNullOrWhiteSpace(error.Source) ? "Error" : error.Source.Trim())).Append("</h3>");
            else if (!string.IsNullOrWhiteSpace(error.Source))
                html.Append("<p><strong>Source:</strong> ").Append(Encode(error.Source.Trim())).Append("</p>");
            html.Append("<p><strong>Occurred:</strong> ").Append(Encode(Local(error.OccurredAt)));
            if (error.Occurrences > 1)
                html.Append(" · <strong>Reported ").Append(error.Occurrences).Append(" times</strong>");
            html.Append("</p>");
            // The message and stack trace exactly as received, keeping their line breaks.
            html.Append("<pre><code>").Append(Encode(error.Message)).Append("</code></pre>");
        }

        return html.ToString();
    }

    private string Local(DateTimeOffset time) => HubTime.Format(time, hubOptions.Value.TimeZone);

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
