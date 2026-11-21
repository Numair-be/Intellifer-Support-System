using Intellifer.Hub.Application;
using Intellifer.Hub.Infrastructure.Odoo;
using Microsoft.Extensions.Logging;

namespace Intellifer.Hub.Infrastructure;

/// <summary>
/// Who works on a project: the employees assigned to it in Odoo (project.project → assigned_employee_ids),
/// reachable by work mobile, phone or email.
/// </summary>
public sealed class OdooTeamDirectory(OdooCustomerDirectory odoo, ILogger<OdooTeamDirectory> logger) : IProjectTeamDirectory
{
    public bool IsConfigured => odoo.IsConfigured;

    public async Task<IReadOnlyList<TeamMember>> GetTeamAsync(IReadOnlyList<string> projectNames, CancellationToken cancellationToken)
    {
        // An Odoo error is thrown on purpose: the team notifier retries later.
        var employees = await odoo.GetProjectEmployeesAsync(projectNames, cancellationToken);
        if (employees is null)
            return [];

        var reachable = employees
            .Where(member => !string.IsNullOrWhiteSpace(member.Phone) || !string.IsNullOrWhiteSpace(member.Email))
            .ToList();
        if (reachable.Count < employees.Count)
            logger.LogInformation(
                "{Count} employee(s) on {Project} in Odoo have no work phone or email",
                employees.Count - reachable.Count,
                projectNames.FirstOrDefault());
        return reachable;
    }
}
