namespace Intellifer.Hub.Application;

/// <summary>An employee assigned to a client project. Phone in any format (it is normalised), email optional.</summary>
public sealed record TeamMember(string Name, string? Email, string? Phone);

/// <summary>
/// Who works on a project: the employees assigned to it in Odoo. <see cref="UnconfiguredProjectTeamDirectory"/> turns team notifications off.
/// </summary>
public interface IProjectTeamDirectory
{
    bool IsConfigured { get; }

    /// <summary>
    /// The employees of the project known by any of these names (Plane project name, Odoo client project name
    /// or code), matched ignoring case, spaces and punctuation.
    /// </summary>
    Task<IReadOnlyList<TeamMember>> GetTeamAsync(IReadOnlyList<string> projectNames, CancellationToken cancellationToken);
}

/// <summary>Placeholder until the Odoo employee API is connected: no team, so nobody is notified.</summary>
public sealed class UnconfiguredProjectTeamDirectory : IProjectTeamDirectory
{
    public bool IsConfigured => false;

    public Task<IReadOnlyList<TeamMember>> GetTeamAsync(IReadOnlyList<string> projectNames, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TeamMember>>([]);
}

/// <summary>A new intake item whose project team still has to be told.</summary>
public sealed record TeamNotificationDraft(
    Guid? ConversationId,
    string ClientProject,
    string? ClientProjectCode,
    string PlaneProject,
    Guid PlaneProjectId,
    string TicketNumber,
    string Title,
    string Customer,
    string CustomerKey);

public sealed record TeamNotificationJob(
    Guid Id,
    Guid? ConversationId,
    string ClientProject,
    string? ClientProjectCode,
    string PlaneProject,
    Guid PlaneProjectId,
    string TicketNumber,
    string Title,
    string Customer,
    string CustomerKey,
    int Attempts);

public interface ITeamNotificationStore
{
    Task<IReadOnlyList<TeamNotificationJob>> GetDueAsync(int take, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Queues the messages to the team and closes the notification, in one transaction.</summary>
    Task CompleteAsync(Guid id, IReadOnlyList<OutboundDraft> messages, string? note, DateTimeOffset now, CancellationToken cancellationToken);

    Task MarkFailedAsync(Guid id, string error, DateTimeOffset? retryAt, CancellationToken cancellationToken);
}
