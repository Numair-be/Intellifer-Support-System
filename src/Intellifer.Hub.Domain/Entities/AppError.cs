namespace Intellifer.Hub.Domain.Entities;

/// <summary>
/// An error reported by an app on the server, on its way to a Plane intake item. Errors of the same project that
/// arrive within the batch window share one ticket (same <see cref="PlaneWorkItemId"/>).
/// </summary>
public sealed class AppError
{
    public Guid Id { get; set; }

    /// <summary>The project name the app sent ("projectName"): the Plane and Odoo project it belongs to.</summary>
    public string Project { get; set; } = "";

    /// <summary><see cref="Project"/> without case, spaces and punctuation, to group and match reports.</summary>
    public string ProjectKey { get; set; } = "";

    /// <summary>Where in the app it happened, e.g. "DataParserController.ExtractTables".</summary>
    public string? Source { get; set; }

    /// <summary>Error message and stack trace as received.</summary>
    public string Message { get; set; } = "";

    /// <summary>Source and message with numbers and spacing normalised, to recognise the same error repeating.</summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>When it happened, as the app reports it (or when it arrived).</summary>
    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>How many times the same error was reported. Duplicates are counted here, not stored again.</summary>
    public int Occurrences { get; set; } = 1;

    public DateTimeOffset LastSeenAt { get; set; }

    public PlaneSyncState PlaneState { get; set; } = PlaneSyncState.Pending;

    public int PlaneAttempts { get; set; }

    public DateTimeOffset? PlaneNextAttemptAt { get; set; }

    /// <summary>Why it was skipped, or the last error while sending.</summary>
    public string? PlaneError { get; set; }

    public Guid? PlaneProjectId { get; set; }

    public Guid? PlaneWorkItemId { get; set; }

    public int? PlaneSequenceId { get; set; }

    public DateTimeOffset? TicketedAt { get; set; }
}
