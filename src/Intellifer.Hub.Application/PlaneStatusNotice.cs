namespace Intellifer.Hub.Application;

/// <summary>Where an intake item is in Plane: its intake decision and its workflow state.</summary>
/// <param name="IntakeStatus">-2 pending, -1 declined, 0 snoozed, 1 accepted, 2 duplicate.</param>
/// <param name="StateGroup">Plane state group: triage, backlog, unstarted, started, completed, cancelled.</param>
public sealed record PlaneItemStatus(int? IntakeStatus, string? StateName, string? StateGroup)
{
    public const int Pending = -2;
    public const int Declined = -1;
    public const int Snoozed = 0;
    public const int Accepted = 1;
    public const int Duplicate = 2;

    /// <summary>Compact form stored on the conversation, e.g. "1:started".</summary>
    public string Key => $"{IntakeStatus?.ToString() ?? "?"}:{(StateGroup ?? "").ToLowerInvariant()}";

    /// <summary>Nothing more will happen: stop polling.</summary>
    public bool IsClosed =>
        IntakeStatus is Declined or Duplicate
        || (IntakeStatus is Accepted && StateGroup?.ToLowerInvariant() is "completed" or "cancelled");

    public static (int? Intake, string Group) ParseKey(string? key)
    {
        var parts = (key ?? "").Split(':', 2);
        var intake = int.TryParse(parts[0], out var value) ? value : (int?)null;
        return (intake, parts.Length > 1 ? parts[1] : "");
    }
}

/// <summary>Turns a Plane status change into the sentence a customer is told, or nothing.</summary>
public static class PlaneStatusNotice
{
    /// <summary>What happened to the request, completing "Your request PLANE-9 ...". Null means do not notify.</summary>
    public static string? Describe(string? previousKey, PlaneItemStatus current)
    {
        if (previousKey == current.Key)
            return null;

        var (previousIntake, previousGroup) = PlaneItemStatus.ParseKey(previousKey);
        var group = (current.StateGroup ?? "").ToLowerInvariant();
        var state = string.IsNullOrWhiteSpace(current.StateName) ? null : current.StateName.Trim();

        switch (current.IntakeStatus)
        {
            case PlaneItemStatus.Declined:
                return "has been reviewed by our team and will not be taken forward. "
                    + "If you think this is a mistake or have more details, just reply to this message.";
            case PlaneItemStatus.Duplicate:
                return "is already being tracked under an existing request, so we have linked it there. "
                    + "We will keep you updated on that one.";
            case PlaneItemStatus.Accepted:
                break;
            default:
                // Pending or snoozed: nothing the customer needs to hear about.
                return null;
        }

        // Accepting an item moves it from triage straight into a state: say both in one message.
        var justAccepted = previousIntake != PlaneItemStatus.Accepted;
        if (!justAccepted && previousGroup == group)
            return null; // same stage, e.g. renamed or moved between two states of the same group

        var stage = group switch
        {
            "backlog" => "is in our backlog",
            "unstarted" => "is planned and on our to-do list",
            "started" => "is now in progress",
            "completed" => "has been completed",
            "cancelled" => "has been cancelled",
            _ => null
        };
        var named = stage is null || state is null ? stage : $"{stage} ({state})";

        if (justAccepted)
            return named is null
                ? "has been accepted by our team."
                : $"has been accepted by our team and {named.Replace("is in", "added to", StringComparison.Ordinal)}.";
        return named is null ? null : $"{named}.";
    }
}
