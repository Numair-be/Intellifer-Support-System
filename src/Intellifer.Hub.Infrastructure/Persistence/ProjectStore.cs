using Intellifer.Hub.Application;
using Intellifer.Hub.Domain;
using Intellifer.Hub.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Intellifer.Hub.Infrastructure.Persistence;

/// <summary>
/// The hub's own project list (project page) and, from it, who works on each project.
/// Projects are few, so name matching (ignoring case, spaces and punctuation) is done in memory.
/// </summary>
public sealed class ProjectStore(HubDbContext db) : IProjectAdmin, IProjectTeamDirectory
{
    public bool IsConfigured => true;

    public async Task<IReadOnlyList<TeamMember>> GetTeamAsync(IReadOnlyList<string> projectNames, CancellationToken cancellationToken)
    {
        var wanted = projectNames.Select(PlaneProjectResolver.Normalize).Where(name => name.Length > 0).ToHashSet();
        var projects = await db.TeamProjects.AsNoTracking().Select(project => new { project.Id, project.Name }).ToListAsync(cancellationToken);
        var match = projects.FirstOrDefault(project => wanted.Contains(PlaneProjectResolver.Normalize(project.Name)));
        if (match is null)
            return [];

        return await db.ProjectMembers
            .Where(member => member.ProjectId == match.Id)
            .OrderBy(member => member.Name)
            .Select(member => new TeamMember(member.Name, null, member.WhatsApp))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectSummary>> ListAsync(CancellationToken cancellationToken) =>
        await db.TeamProjects
            .OrderBy(project => project.Name)
            .Select(project => new ProjectSummary(project.Id, project.Name, project.Notes, project.Members.Count, project.UpdatedAt))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task<ProjectDetail?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = await db.TeamProjects.Include(item => item.Members).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return project is null
            ? null
            : new ProjectDetail(
                project.Id,
                project.Name,
                project.Notes,
                project.CreatedAt,
                project.UpdatedAt,
                project.Members.OrderBy(member => member.Name).Select(ToInfo).ToList());
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

    public async Task<ProjectMemberInfo?> AddMemberAsync(Guid projectId, string name, string whatsApp, CancellationToken cancellationToken)
    {
        if (!await db.TeamProjects.AnyAsync(project => project.Id == projectId, cancellationToken))
            return null;

        var member = new ProjectMember
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = TextClip.Clip(name.Trim(), 200),
            WhatsApp = whatsApp,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ProjectMembers.Add(member);
        await TouchAsync(projectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToInfo(member);
    }

    public async Task<ProjectMemberInfo?> UpdateMemberAsync(Guid projectId, Guid memberId, string name, string whatsApp, CancellationToken cancellationToken)
    {
        var member = await db.ProjectMembers.FirstOrDefaultAsync(item => item.Id == memberId && item.ProjectId == projectId, cancellationToken);
        if (member is null)
            return null;
        member.Name = TextClip.Clip(name.Trim(), 200);
        member.WhatsApp = whatsApp;
        await TouchAsync(projectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToInfo(member);
    }

    public async Task<bool> DeleteMemberAsync(Guid projectId, Guid memberId, CancellationToken cancellationToken)
    {
        var deleted = await db.ProjectMembers.Where(item => item.Id == memberId && item.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
            await db.TeamProjects.Where(project => project.Id == projectId)
                .ExecuteUpdateAsync(update => update.SetProperty(project => project.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
        return deleted > 0;
    }

    private async Task<bool> NameTakenAsync(string name, Guid? except, CancellationToken cancellationToken)
    {
        var key = PlaneProjectResolver.Normalize(name);
        var names = await db.TeamProjects.Where(project => project.Id != except).Select(project => project.Name).ToListAsync(cancellationToken);
        return names.Any(existing => PlaneProjectResolver.Normalize(existing) == key);
    }

    private async Task TouchAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.TeamProjects.FirstAsync(item => item.Id == projectId, cancellationToken);
        project.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static ProjectMemberInfo ToInfo(ProjectMember member) => new(member.Id, member.Name, member.WhatsApp);
}
