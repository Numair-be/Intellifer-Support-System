using Intellifer.Hub.Application.Options;
using Microsoft.Extensions.Options;

namespace Intellifer.Hub.Application;

/// <summary>
/// Finds the Plane project for an Odoo client project. A Plane project matches when its name or identifier
/// equals the Odoo project's name or code, ignoring case, spaces and punctuation ("LT Form IV-D" = "LT FORM IV D").
/// PLANE_PROJECT_MAP overrides this when the names differ. The Plane list is cached, so new Plane projects
/// are picked up within a few minutes without any setup.
/// </summary>
public sealed class PlaneProjectResolver(IPlaneGateway plane, IOptions<PlaneOptions> options, TimeProvider clock)
{
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private IReadOnlyList<PlaneProject>? _projects;
    private DateTimeOffset _loadedAt;

    public async Task<IReadOnlyList<PlaneProject>> GetProjectsAsync(CancellationToken cancellationToken)
    {
        var maxAge = TimeSpan.FromMinutes(Math.Clamp(options.Value.ProjectCacheMinutes, 1, 1_440));
        if (_projects is not null && clock.GetUtcNow() - _loadedAt < maxAge)
            return _projects;

        await _refresh.WaitAsync(cancellationToken);
        try
        {
            if (_projects is null || clock.GetUtcNow() - _loadedAt >= maxAge)
            {
                _projects = await plane.ListProjectsAsync(cancellationToken);
                _loadedAt = clock.GetUtcNow();
            }

            return _projects;
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>The first of the contact's Odoo projects that has a Plane project, or null.</summary>
    public async Task<(ClientProject Odoo, PlaneProject Plane)?> ResolveAsync(
        IEnumerable<ClientProject> clientProjects,
        CancellationToken cancellationToken)
    {
        var projects = await GetProjectsAsync(cancellationToken);
        var overrides = ParseMap(options.Value.ProjectMap);
        foreach (var client in clientProjects)
        {
            var match = Find(client, projects, overrides);
            if (match is not null)
                return (client, match);
        }

        return null;
    }

    public static PlaneProject? Find(
        ClientProject client,
        IReadOnlyList<PlaneProject> projects,
        IReadOnlyDictionary<string, string> overrides)
    {
        // An explicit mapping wins over name matching.
        foreach (var key in new[] { client.Name, client.Code })
        {
            if (key is not null && overrides.TryGetValue(Normalize(key), out var target))
                return projects.FirstOrDefault(project => Normalize(project.Name) == target || Normalize(project.Identifier) == target);
        }

        var wanted = new[] { client.Name, client.Code }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Normalize(value!))
            .ToHashSet();
        // Prefer a name match, then an identifier match, so "KSB" never steals a project named differently.
        return projects.FirstOrDefault(project => wanted.Contains(Normalize(project.Name)))
            ?? projects.FirstOrDefault(project => wanted.Contains(Normalize(project.Identifier)));
    }

    public static IReadOnlyDictionary<string, string> ParseMap(string? map)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in (map ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
                result[Normalize(parts[0])] = Normalize(parts[1]);
        }

        return result;
    }

    public static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
