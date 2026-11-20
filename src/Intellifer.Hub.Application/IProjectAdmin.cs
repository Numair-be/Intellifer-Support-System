namespace Intellifer.Hub.Application;

public sealed record ProjectSummary(Guid Id, string Name, string? Notes, int MemberCount, DateTimeOffset UpdatedAt);

public sealed record ProjectMemberInfo(Guid Id, string Name, string WhatsApp);

public sealed record ProjectDetail(Guid Id, string Name, string? Notes, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ProjectMemberInfo> Members);

public enum ProjectSaveResult
{
    Saved,
    NotFound,
    NameTaken
}

/// <summary>Projects, their employees, for the hub's project page.</summary>
public interface IProjectAdmin
{
    Task<IReadOnlyList<ProjectSummary>> ListAsync(CancellationToken cancellationToken);

    Task<ProjectDetail?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<(ProjectSaveResult Result, Guid Id)> CreateAsync(string name, string? notes, CancellationToken cancellationToken);

    /// <summary>Renaming also moves the apps connected under the old name.</summary>
    Task<ProjectSaveResult> UpdateAsync(Guid id, string name, string? notes, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Null when the project does not exist.</summary>
    Task<ProjectMemberInfo?> AddMemberAsync(Guid projectId, string name, string whatsApp, CancellationToken cancellationToken);

    Task<ProjectMemberInfo?> UpdateMemberAsync(Guid projectId, Guid memberId, string name, string whatsApp, CancellationToken cancellationToken);

    Task<bool> DeleteMemberAsync(Guid projectId, Guid memberId, CancellationToken cancellationToken);
}
