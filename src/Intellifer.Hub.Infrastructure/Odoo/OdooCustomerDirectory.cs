using System.Text.Json;
using System.Text.Json.Nodes;
using Intellifer.Hub.Application;
using Intellifer.Hub.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intellifer.Hub.Infrastructure.Odoo;

public sealed class OdooRpcException(string message) : Exception(message);

/// <summary>
/// Reads Odoo through the standard JSON-RPC endpoint (/jsonrpc) with an API key. Read-only:
/// client.contact (who writes in, and their projects), project.project (projects and their assigned employees)
/// and hr.employee (employee phones and emails).
/// </summary>
public sealed class OdooCustomerDirectory(
    IHttpClientFactory httpClients,
    IOptions<OdooOptions> options,
    CustomerMatcher matcher,
    ILogger<OdooCustomerDirectory> logger) : ICustomerDirectory
{
    public const string HttpClientName = "odoo";

    private const string ContactModel = "client.contact";
    private const string ProjectModel = "project.project";
    private const string EmployeeModel = "hr.employee";

    // Odoo phones are free text, so search by the last digits and let CustomerMatcher compare exactly.
    private const int PhoneSuffixDigits = 4;
    private const int CandidateLimit = 100;

    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private int? _uid;
    private int _requestId;

    public bool IsConfigured => options.Value.IsConfigured;

    public Task<CustomerMatch?> FindAsync(CustomerKey key, CancellationToken cancellationToken) =>
        IsConfigured
            ? WithLoginAsync(uid => FindOnceAsync(uid, key, cancellationToken), cancellationToken)
            : Task.FromResult<CustomerMatch?>(null);

    public Task<IReadOnlyList<ClientProject>> ListProjectsAsync(CancellationToken cancellationToken) =>
        IsConfigured
            ? WithLoginAsync<IReadOnlyList<ClientProject>>(
                async uid => (await ReadProjectsAsync(uid, null, cancellationToken)).Select(project => new ClientProject(project.Name)).ToList(),
                cancellationToken)
            : Task.FromResult<IReadOnlyList<ClientProject>>([]);

    /// <summary>
    /// The employees assigned to the Odoo project known by any of <paramref name="projectNames"/> (ignoring case,
    /// spaces and punctuation). Null when Odoo has no such project.
    /// </summary>
    public Task<IReadOnlyList<TeamMember>?> GetProjectEmployeesAsync(IReadOnlyList<string> projectNames, CancellationToken cancellationToken) =>
        IsConfigured
            ? WithLoginAsync(uid => ReadProjectEmployeesAsync(uid, projectNames, cancellationToken), cancellationToken)
            : Task.FromResult<IReadOnlyList<TeamMember>?>(null);

    private async Task<T> WithLoginAsync<T>(Func<int, Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call(await GetUidAsync(cancellationToken));
        }
        catch (OdooRpcException exception) when (IsSessionProblem(exception))
        {
            // The stored uid can go stale if the key is rotated; sign in again once.
            logger.LogInformation("Odoo rejected the cached login, signing in again: {Error}", exception.Message);
            _uid = null;
            return await call(await GetUidAsync(cancellationToken));
        }
    }

    private async Task<CustomerMatch?> FindOnceAsync(int uid, CustomerKey key, CancellationToken cancellationToken)
    {
        // "=ilike" with no wildcards is an exact, case-insensitive match.
        var domain = key.Kind == CustomerKeyKind.Email
            ? new JsonArray("|", new JsonArray("email", "=ilike", key.Value), new JsonArray("alternate_email", "=ilike", key.Value))
            : new JsonArray(new JsonArray("phone", "ilike", key.PhoneSuffix(PhoneSuffixDigits)));

        var result = await ExecuteAsync(
            uid,
            ContactModel,
            "search_read",
            new JsonArray(domain),
            new JsonObject
            {
                ["fields"] = new JsonArray("id", "name", "company_name", "email", "alternate_email", "phone", "project_ids"),
                ["limit"] = CandidateLimit,
                ["order"] = "id"
            },
            cancellationToken);

        var rows = result.EnumerateArray().ToList();
        // project_ids holds ids only: read the project names in one more call.
        var projectIds = rows.SelectMany(row => Ids(row, "project_ids")).Distinct().ToList();
        var names = projectIds.Count == 0
            ? new Dictionary<int, string>()
            : (await ReadProjectsAsync(uid, projectIds, cancellationToken)).ToDictionary(project => project.Id, project => project.Name);

        var candidates = rows.Select(row => new ContactCandidate(
                row.GetProperty("id").GetInt32(),
                Text(row, "name") ?? "",
                Text(row, "company_name"),
                Ids(row, "project_ids").Where(names.ContainsKey).Select(id => new ClientProject(names[id])).ToList(),
                new[] { Text(row, "email"), Text(row, "alternate_email") }.OfType<string>().ToList(),
                new[] { Text(row, "phone") }.OfType<string>().ToList()))
            .ToList();
        return matcher.Pick(key, candidates);
    }

    private sealed record OdooProject(int Id, string Name, IReadOnlyList<int> EmployeeIds);

    private async Task<IReadOnlyList<OdooProject>> ReadProjectsAsync(int uid, IReadOnlyList<int>? ids, CancellationToken cancellationToken)
    {
        var domain = ids is null
            ? new JsonArray()
            : new JsonArray(new JsonArray("id", "in", new JsonArray(ids.Select(id => (JsonNode)id).ToArray())));
        var result = await ExecuteAsync(
            uid,
            ProjectModel,
            "search_read",
            new JsonArray(domain),
            new JsonObject { ["fields"] = new JsonArray("id", "name", "assigned_employee_ids"), ["order"] = "name" },
            cancellationToken);
        return result.EnumerateArray()
            .Where(row => Text(row, "name") is not null)
            .Select(row => new OdooProject(row.GetProperty("id").GetInt32(), Text(row, "name")!, Ids(row, "assigned_employee_ids").ToList()))
            .ToList();
    }

    private async Task<IReadOnlyList<TeamMember>?> ReadProjectEmployeesAsync(
        int uid,
        IReadOnlyList<string> projectNames,
        CancellationToken cancellationToken)
    {
        var wanted = projectNames.Select(PlaneProjectResolver.Normalize).Where(name => name.Length > 0).ToHashSet();
        var project = (await ReadProjectsAsync(uid, null, cancellationToken))
            .FirstOrDefault(item => wanted.Contains(PlaneProjectResolver.Normalize(item.Name)));
        if (project is null)
            return null;
        if (project.EmployeeIds.Count == 0)
            return [];

        var result = await ExecuteAsync(
            uid,
            EmployeeModel,
            "read",
            new JsonArray(new JsonArray(project.EmployeeIds.Select(id => (JsonNode)id).ToArray())),
            new JsonObject { ["fields"] = new JsonArray("name", "mobile_phone", "phone", "work_phone", "work_email", "email") },
            cancellationToken);
        // Work numbers only, never private ones: Work Mobile, then Phone, then Work Phone.
        return result.EnumerateArray()
            .Select(row => new TeamMember(
                Text(row, "name") ?? "",
                Text(row, "work_email") ?? Text(row, "email"),
                Text(row, "mobile_phone") ?? Text(row, "phone") ?? Text(row, "work_phone")))
            .ToList();
    }

    // many2many fields come back as a list of ids.
    private static IEnumerable<int> Ids(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(id => id.ValueKind == JsonValueKind.Number).Select(id => id.GetInt32()).ToList()
            : [];

    // Odoo returns false, not null, for empty fields.
    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private async Task<int> GetUidAsync(CancellationToken cancellationToken)
    {
        if (_uid is { } cached)
            return cached;

        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            if (_uid is { } again)
                return again;

            var settings = options.Value;
            var result = await CallAsync(
                "common",
                "authenticate",
                new JsonArray(settings.Database, settings.Login, settings.ApiKey, new JsonObject()),
                cancellationToken);
            if (result.ValueKind != JsonValueKind.Number)
                throw new OdooRpcException(
                    $"Odoo rejected the login for {settings.Login} on database {settings.Database}. Check ODOO_LOGIN and ODOO_API_KEY.");

            _uid = result.GetInt32();
            logger.LogInformation("Signed in to Odoo database {Database} as user {Uid}", settings.Database, _uid);
            return _uid.Value;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private Task<JsonElement> ExecuteAsync(
        int uid,
        string model,
        string method,
        JsonArray args,
        JsonObject kwargs,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        return CallAsync(
            "object",
            "execute_kw",
            new JsonArray(settings.Database, uid, settings.ApiKey, model, method, args, kwargs),
            cancellationToken);
    }

    private async Task<JsonElement> CallAsync(string service, string method, JsonArray args, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "call",
            ["params"] = new JsonObject
            {
                ["service"] = service,
                ["method"] = method,
                ["args"] = args
            },
            ["id"] = Interlocked.Increment(ref _requestId)
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 2, 60)));

        var client = httpClients.CreateClient(HttpClientName);
        var url = settings.Url!.TrimEnd('/') + "/jsonrpc";
        // Plain string body so Content-Length is set; some proxies drop chunked request bodies.
        using var content = new StringContent(request.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(url, content, timeout.Token);
        response.EnsureSuccessStatusCode();

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(timeout.Token),
            cancellationToken: timeout.Token);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
            throw new OdooRpcException(ErrorMessage(error));
        if (!root.TryGetProperty("result", out var result))
            throw new OdooRpcException("Odoo returned no result.");
        return result.Clone();
    }

    private static string ErrorMessage(JsonElement error)
    {
        if (error.TryGetProperty("data", out var data) && data.TryGetProperty("message", out var detail)
            && detail.ValueKind == JsonValueKind.String)
            return detail.GetString()!;
        return error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
            ? message.GetString()!
            : "Odoo returned an error.";
    }

    private static bool IsSessionProblem(OdooRpcException exception) =>
        exception.Message.Contains("Access Denied", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("session", StringComparison.OrdinalIgnoreCase);
}
