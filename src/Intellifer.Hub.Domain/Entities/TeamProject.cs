namespace Intellifer.Hub.Domain.Entities;

/// <summary>
/// A project as the hub's project page knows it: same name as the Plane project, and (through apps.project)
/// the apps that report errors to it. Its employees come from Odoo.
/// </summary>
public sealed class TeamProject
{
    public Guid Id { get; set; }

    /// <summary>Same as the Plane project name (matched ignoring case, spaces and punctuation).</summary>
    public string Name { get; set; } = "";

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
