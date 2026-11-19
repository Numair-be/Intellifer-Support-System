using Intellifer.Hub.Domain;

namespace Intellifer.Hub.Application;

/// <summary>An Odoo client.project: its name and optional short code.</summary>
public sealed record ClientProject(string Name, string? Code = null)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Code) ? Name : $"{Name} ({Code})";
}

/// <summary>
/// The client contact behind a sender. <see cref="Projects"/> lists the project of every active contact record
/// that has this email or phone, because one person can be a contact on several projects.
/// </summary>
public sealed record CustomerMatch(int ContactId, string Name, string? CompanyName, IReadOnlyList<ClientProject> Projects);

/// <summary>Read-only lookup of the client behind a sender. Odoo's client.contact is the only implementation.</summary>
public interface ICustomerDirectory
{
    bool IsConfigured { get; }

    /// <summary>Returns the matching client contact, or null when no active contact has this email or phone.</summary>
    Task<CustomerMatch?> FindAsync(CustomerKey key, CancellationToken cancellationToken);

    /// <summary>All active client projects, for showing how they map to Plane.</summary>
    Task<IReadOnlyList<ClientProject>> ListProjectsAsync(CancellationToken cancellationToken);
}
