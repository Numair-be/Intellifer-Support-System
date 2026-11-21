using Intellifer.Hub.Application;
using Intellifer.Hub.Domain;
using Intellifer.Hub.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Intellifer.Hub.Infrastructure.Persistence;

/// <summary>
/// The hub's own project list (project page), used to link apps to a project.
/// Projects are few, so name matching (ignoring case, spaces and punctuation) is done in memory.
/// </summary>
public sealed class ProjectStore(HubDbContext db) : IProjectAdmin
{
    public async Task<IReadOnlyList<ProjectSummary>> ListAsync(CancellationToken cancellationToken) =>
        await db.TeamProjects
            .OrderBy(project => project.Name)
            .Select(project => new ProjectSummary(project.Id, project.Name, project.Notes, project.UpdatedAt))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task<ProjectDetail?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = await db.TeamProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return project is null
            ? null
            : new ProjectDetail(
                project.Id,
                project.Name,
                project.Notes,
                project.CreatedAt,
                project.UpdatedAt);
    }

    public async Task<(ProjectSaveResult Result, Guid Id)> CreateAsync(string name, string? notes, CancellationToken cancellationToken)
    {
        if (await NameTakenAsync(name, null, cancellationToken))
            return (ProjectSaveResult.NameTaken, Guid.Empty);

        var now = DateTimeOffset.UtcNow;
        var project = new TeamProject
        {
            Id = Guid.NewGuid(),
            Name = TextClip.Clip(name.Trim(), 256),
            Notes = TextClip.ClipOrNull(string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), 2_000),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.TeamProjects.Add(project);
        await db.SaveChangesAsync(cancellationToken);
        return (ProjectSaveResult.Saved, project.Id);
    }

    public async Task<ProjectSaveResult> UpdateAsync(Guid id, string name, string? notes, CancellationToken cancellationToken)
    {
        var project = await db.TeamProjects.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (project is null)
            return ProjectSaveResult.NotFound;
        if (await NameTakenAsync(name, id, cancellationToken))
            return ProjectSaveResult.NameTaken;

        var oldName = project.Name;
        project.Name = TextClip.Clip(name.Trim(), 256);
        project.Notes = TextClip.ClipOrNull(string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), 2_000);
        project.UpdatedAt = DateTimeOffset.UtcNow;

        // Apps are linked by project name: move the ones connected under the old name.
        if (!string.Equals(oldName, project.Name, StringComparison.Ordinal))
        {
            var oldKey = PlaneProjectResolver.Normalize(oldName);
            var apps = await db.Apps.ToListAsync(cancellationToken);
            foreach (var app in apps.Where(app => PlaneProjectResolver.Normalize(app.Project) == oldKey))
                app.Project = project.Name;
        }

        await db.SaveChangesAsync(cancellationToken);
        return ProjectSaveResult.Saved;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        await db.TeamProjects.Where(project => project.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;

    private async Task<bool> NameTakenAsync(string name, Guid? except, CancellationToken cancellationToken)
    {
        var key = PlaneProjectResolver.Normalize(name);
        var names = await db.TeamProjects.Where(project => project.Id != except).Select(project => project.Name).ToListAsync(cancellationToken);
        return names.Any(existing => PlaneProjectResolver.Normalize(existing) == key);
    }
}
