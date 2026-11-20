namespace Intellifer.Hub.Domain.Entities;

/// <summary>
/// An application on the server that may report errors to the hub, and the project its errors belong to.
/// Temporary home of the app-to-project link until it moves to Odoo.
/// </summary>
public sealed class AppRegistration
{
    public Guid Id { get; set; }

    /// <summary>Unique, e.g. "crm-backend". Shown in the ticket title as where the error came from.</summary>
    public string Name { get; set; } = "";

    /// <summary>Project name or code, matched to a Plane project the same way as Odoo client projects.</summary>
    public string Project { get; set; } = "";

    /// <summary>SHA-256 of the app's key, hex. The key itself is only shown once, when created.</summary>
    public string KeyHash { get; set; } = "";

    /// <summary>The first characters of the key, to recognise it in a list.</summary>
    public string KeyHint { get; set; } = "";

    public bool Active { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastErrorAt { get; set; }
}

/// <summary>An error reported by an app, on its way to a Plane intake item.</summary>
public sealed class AppError
{
    public Guid Id { get; set; }

    public Guid AppId { get; set; }

    public AppRegistration App { get; set; } = null!;

    public string Message { get; set; } = "";

    /// <summary>Optional: which part of the app it came from (module, host, job ...), as the app sends it.</summary>
    public string? Source { get; set; }

    public string? Level { get; set; }

    /// <summary>The message with numbers and spacing normalised, to recognise the same error repeating.</summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>When it happened, as the app reports it (or when it arrived).</summary>
    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>How many times the same error arrived within the repeat window. Repeats do not open new tickets.</summary>
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
}
