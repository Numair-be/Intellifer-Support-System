namespace Intellifer.Hub.Domain.Entities;

/// <summary>
/// A project as the hub's project page knows it: same name as the Plane project, the employees who work on it,
/// and (through apps.project) the apps that report errors to it. Stand-in until the Odoo employee API exists.
/// </summary>
public sealed class TeamProject
{
    public Guid Id { get; set; }

    /// <summary>Same as the Plane project name (matched ignoring case, spaces and punctuation).</summary>
    public string Name { get; set; } = "";

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ProjectMember> Members { get; set; } = [];
}

/// <summary>An employee on a project, told on WhatsApp when an intake item is created for it.</summary>
public sealed class ProjectMember
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public TeamProject Project { get; set; } = null!;

    public string Name { get; set; } = "";

    /// <summary>E.164, e.g. +919876543210.</summary>
    public string WhatsApp { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}
