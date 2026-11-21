namespace Intellifer.Hub.Application;

public sealed record ProjectSummary(Guid Id, string Name, string? Notes, DateTimeOffset UpdatedAt);

public sealed record ProjectDetail(Guid Id, string Name, string? Notes, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public enum ProjectSaveResult
{
    Saved,
    NotFound,
    NameTaken
}

/// <summary>Projects for the hub's project page: the Plane project name that connected apps report errors to.
/// Employees come from Odoo, not from here.</summary>
public interface IProjectAdmin
{
    Task<IReadOnlyList<ProjectSummary>> ListAsync(CancellationToken cancellationToken);

    Task<ProjectDetail?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<(ProjectSaveResult Result, Guid Id)> CreateAsync(string name, string? notes, CancellationToken cancellationToken);

    /// <summary>Renaming also moves the apps connected under the old name.</summary>
    Task<ProjectSaveResult> UpdateAsync(Guid id, string name, string? notes, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
