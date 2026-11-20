using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Intellifer.Hub.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intellifer.Hub.Application;

public sealed record AppInfo(Guid Id, string Name, string Project, string KeyHint, bool Active, DateTimeOffset CreatedAt, DateTimeOffset? LastErrorAt);

/// <summary>The apps allowed to report errors, and the project each belongs to.</summary>
public interface IAppRegistry
{
    Task<IReadOnlyList<AppInfo>> ListAsync(CancellationToken cancellationToken);

    Task<AppInfo?> FindByKeyHashAsync(string keyHash, CancellationToken cancellationToken);

    /// <summary>Null when an app with that name already exists.</summary>
    Task<AppInfo?> CreateAsync(string name, string project, string keyHash, string keyHint, CancellationToken cancellationToken);

    Task<AppInfo?> UpdateAsync(Guid id, string? project, bool? active, CancellationToken cancellationToken);

    Task<AppInfo?> ReplaceKeyAsync(Guid id, string keyHash, string keyHint, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

public sealed record AppErrorReport(string Message, string? Source, string? Level, DateTimeOffset? OccurredAt);

public sealed record AppErrorAccepted(Guid ErrorId, bool Repeat, int Occurrences, string Status);

public sealed record AppErrorJob(
    Guid Id,
    string AppName,
    string Project,
    string Message,
    string? Source,
    string? Level,
    DateTimeOffset OccurredAt,
    int Occurrences,
    int Attempts);

public interface IAppErrorStore
{
    /// <summary>The newest error of this app with this fingerprint received since <paramref name="since"/>.</summary>
    Task<Guid?> FindRecentAsync(Guid appId, string fingerprint, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Counts one more occurrence of an existing error; returns the new count.</summary>
    Task<int> CountRepeatAsync(Guid errorId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>New tickets this app produced since <paramref name="since"/> (repeats and skipped errors excluded).</summary>
    Task<int> CountTicketedSinceAsync(Guid appId, DateTimeOffset since, CancellationToken cancellationToken);

    Task<Guid> AddAsync(Guid appId, AppErrorReport report, string fingerprint, DateTimeOffset now, string? skipReason, CancellationToken cancellationToken);

    Task<IReadOnlyList<AppErrorJob>> GetDueAsync(int take, DateTimeOffset now, CancellationToken cancellationToken);

    Task MarkSentAsync(Guid id, Guid projectId, PlaneIntakeCreated created, CancellationToken cancellationToken);

    Task MarkSkippedAsync(Guid id, string reason, CancellationToken cancellationToken);

    Task MarkFailedAsync(Guid id, string error, DateTimeOffset? retryAt, CancellationToken cancellationToken);
}

/// <summary>App keys: "hubapp_" + 40 random characters. Only their SHA-256 is stored.</summary>
public static class AppKeys
{
    public const string Prefix = "hubapp_";

    public static string NewKey()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return Prefix + string.Concat(Enumerable.Range(0, 40).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]));
    }

    public static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim()))).ToLowerInvariant();

    public static string Hint(string key) => key[..Math.Min(key.Length, Prefix.Length + 4)] + "…";
}

/// <summary>Receives an error from an app: stores it, folds repeats into one, and caps error storms.</summary>
public sealed partial class AppErrorIntake(
    IAppErrorStore store,
    IOptions<HubOptions> options,
    TimeProvider clock,
    ILogger<AppErrorIntake> logger)
{
    public const int MaxMessageLength = 100_000;

    public async Task<AppErrorAccepted> AcceptAsync(AppInfo app, AppErrorReport report, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow();
        var fingerprint = Fingerprint(report.Message);

        var repeatWindow = TimeSpan.FromMinutes(Math.Clamp(settings.AppErrorRepeatMinutes, 0, 24 * 60));
        if (repeatWindow > TimeSpan.Zero && await store.FindRecentAsync(app.Id, fingerprint, now - repeatWindow, cancellationToken) is { } earlier)
        {
            var count = await store.CountRepeatAsync(earlier, now, cancellationToken);
            return new AppErrorAccepted(earlier, true, count, "repeat of an error already reported");
        }

        string? skip = null;
        var ticketed = await store.CountTicketedSinceAsync(app.Id, now - TimeSpan.FromHours(1), cancellationToken);
        if (ticketed >= Math.Max(1, settings.AppErrorMaxTicketsPerHour))
        {
            skip = $"{app.Name} already opened {ticketed} tickets in the last hour (limit {settings.AppErrorMaxTicketsPerHour}).";
            logger.LogWarning("Error storm from {App}: storing without a ticket", app.Name);
        }

        var id = await store.AddAsync(app.Id, report, fingerprint, now, skip, cancellationToken);
        return new AppErrorAccepted(id, false, 1, skip is null ? "queued for Plane" : "stored, not ticketed: " + skip);
    }

    /// <summary>Same error = same text once numbers, hex ids and spacing are ignored (timestamps, ids, ports vary).</summary>
    public static string Fingerprint(string message)
    {
        var text = message.Length > 4_000 ? message[..4_000] : message;
        text = HexOrNumber().Replace(text.ToLowerInvariant(), "#");
        text = Whitespace().Replace(text, " ").Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    [GeneratedRegex(@"\b(0x)?[0-9a-f]*\d[0-9a-f]*\b")]
    private static partial Regex HexOrNumber();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>Turns queued app errors into Plane intake items in the app's project.</summary>
public sealed class AppErrorDispatcher(
    IAppErrorStore store,
    IPlaneGateway plane,
    PlaneProjectResolver projects,
    IOptions<HubOptions> hubOptions,
    TimeProvider clock,
    ILogger<AppErrorDispatcher> logger)
{
    private const int MaxAttempts = 8;

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!plane.IsConfigured)
            return 0;

        var created = 0;
        foreach (var job in await store.GetDueAsync(10, clock.GetUtcNow(), cancellationToken))
        {
            try
            {
                var resolved = await projects.ResolveAsync([new ClientProject(job.Project, job.Project)], cancellationToken);
                if (resolved is null)
                {
                    await store.MarkSkippedAsync(job.Id, $"Project '{job.Project}' of app {job.AppName} has no matching Plane project.", cancellationToken);
                    continue;
                }

                var target = resolved.Value.Plane;
                if (!target.IntakeEnabled)
                {
                    await store.MarkSkippedAsync(job.Id, $"Intake is off in Plane project {target.Name} ({target.Identifier}).", cancellationToken);
                    continue;
                }

                var ticket = await plane.CreateIntakeAsync(target.Id, Title(job), Description(job, target), cancellationToken);
                await store.MarkSentAsync(job.Id, target.Id, ticket, cancellationToken);
                created++;
                logger.LogInformation(
                    "Opened Plane intake {Identifier}-{Sequence} for an error from {App}",
                    target.Identifier,
                    ticket.SequenceId,
                    job.AppName);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var attempts = job.Attempts + 1;
                var error = TextClip.Clip(exception.Message, 1_000);
                DateTimeOffset? retryAt = attempts >= MaxAttempts
                    ? null
                    : clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, attempts - 1), 3_600));
                await store.MarkFailedAsync(job.Id, error, retryAt, cancellationToken);
                logger.LogWarning("Sending the error from {App} to Plane failed (attempt {Attempts}): {Error}", job.AppName, attempts, error);
            }
        }

        return created;
    }

    /// <summary>"[Error] crm-backend – 2026-11-20 18:33 (UTC+05:30)": error, date, and where it came from.</summary>
    public string Title(AppErrorJob job)
    {
        var where = string.IsNullOrWhiteSpace(job.Source) ? job.AppName : $"{job.AppName} / {job.Source.Trim()}";
        return TextClip.Clip($"[Error] {where} – {Local(job.OccurredAt)}", 255);
    }

    public string Description(AppErrorJob job, PlaneProject project)
    {
        var html = new StringBuilder();
        html.Append("<p><strong>App:</strong> ").Append(Encode(job.AppName)).Append("<br>");
        if (!string.IsNullOrWhiteSpace(job.Source))
            html.Append("<strong>Source:</strong> ").Append(Encode(job.Source)).Append("<br>");
        if (!string.IsNullOrWhiteSpace(job.Level))
            html.Append("<strong>Level:</strong> ").Append(Encode(job.Level)).Append("<br>");
        html.Append("<strong>Project:</strong> ").Append(Encode($"{project.Name} ({project.Identifier})")).Append("<br>");
        html.Append("<strong>Occurred:</strong> ").Append(Encode(Local(job.OccurredAt))).Append("</p>");
        if (job.Occurrences > 1)
            html.Append("<p><em>Seen ").Append(job.Occurrences).Append(" times before this ticket was created.</em></p>");
        // The message exactly as received, keeping its line breaks.
        html.Append("<pre><code>").Append(Encode(job.Message)).Append("</code></pre>");
        return html.ToString();
    }

    private string Local(DateTimeOffset time)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(hubOptions.Value.TimeZone);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
        }

        var local = TimeZoneInfo.ConvertTime(time, zone);
        var offset = local.Offset;
        var label = offset == TimeSpan.Zero ? "UTC" : $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}";
        return $"{local:yyyy-MM-dd HH:mm} ({label})";
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
