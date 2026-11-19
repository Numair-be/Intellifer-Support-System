using System.Text.Json;
using System.Text.Json.Nodes;
using Intellifer.Hub.Application;
using Intellifer.Hub.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intellifer.Hub.Infrastructure.Odoo;

public sealed class OdooRpcException(string message) : Exception(message);

/// <summary>
/// Finds client contacts in Odoo through the standard JSON-RPC endpoint (/jsonrpc). Read-only: it only calls
/// authenticate and search_read on client.contact (the custom client module, which links contacts to projects).
/// </summary>
public sealed class OdooCustomerDirectory(
    IHttpClientFactory httpClients,
    IOptions<OdooOptions> options,
    CustomerMatcher matcher,
    ILogger<OdooCustomerDirectory> logger) : ICustomerDirectory
{
    public const string HttpClientName = "odoo";

    private const string ContactModel = "client.contact";

    // Odoo phones are free text, so search by the last digits and let CustomerMatcher compare exactly.
    private const int PhoneSuffixDigits = 4;
    private const int CandidateLimit = 100;

    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private int? _uid;
    private int _requestId;

    public bool IsConfigured => options.Value.IsConfigured;

    public async Task<CustomerMatch?> FindAsync(CustomerKey key, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            return null;

        try
        {
            return await FindOnceAsync(key, cancellationToken);
        }
        catch (OdooRpcException exception) when (IsSessionProblem(exception))
        {
            // The stored uid can go stale if the key is rotated; sign in again once.
            logger.LogInformation("Odoo rejected the cached login, signing in again: {Error}", exception.Message);
            _uid = null;
            return await FindOnceAsync(key, cancellationToken);
        }
    }

    private async Task<CustomerMatch?> FindOnceAsync(CustomerKey key, CancellationToken cancellationToken)
    {
        var uid = await GetUidAsync(cancellationToken);

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
                ["fields"] = new JsonArray("id", "name", "company_name", "email", "alternate_email", "phone", "project_id"),
                ["limit"] = CandidateLimit,
                ["order"] = "id"
            },
            cancellationToken);

        var rows = result.EnumerateArray().ToList();
        var codes = await GetProjectCodesAsync(uid, rows, cancellationToken);
        var candidates = rows.Select(row => ToCandidate(row, codes)).ToList();
        return matcher.Pick(key, candidates);
    }

    public async Task<IReadOnlyList<ClientProject>> ListProjectsAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            return [];
        var uid = await GetUidAsync(cancellationToken);
        var result = await ExecuteAsync(
            uid,
            "client.project",
            "search_read",
            new JsonArray(new JsonArray()),
            new JsonObject { ["fields"] = new JsonArray("name", "code"), ["order"] = "name" },
            cancellationToken);
        return result.EnumerateArray()
            .Where(row => Text(row, "name") is not null)
            .Select(row => new ClientProject(Text(row, "name")!, Text(row, "code")))
            .ToList();
    }

    // client.contact only returns the project's name, so read the codes (e.g. "LNT") in one more call.
    private async Task<Dictionary<int, string>> GetProjectCodesAsync(int uid, List<JsonElement> rows, CancellationToken cancellationToken)
    {
        var ids = rows
            .Select(row => row.TryGetProperty("project_id", out var project) && project.ValueKind == JsonValueKind.Array
                ? project[0].GetInt32()
                : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return [];

        var result = await ExecuteAsync(
            uid,
            "client.project",
            "search_read",
            new JsonArray(new JsonArray(new JsonArray("id", "in", new JsonArray(ids.Select(id => (JsonNode)id).ToArray())))),
            new JsonObject { ["fields"] = new JsonArray("id", "code") },
            cancellationToken);
        return result.EnumerateArray()
            .Where(row => Text(row, "code") is not null)
            .ToDictionary(row => row.GetProperty("id").GetInt32(), row => Text(row, "code")!);
    }

    private static ContactCandidate ToCandidate(JsonElement row, IReadOnlyDictionary<int, string> codes)
    {
        // many2one fields come back as [id, "display name"], or false when empty.
        ClientProject? project = null;
        if (row.TryGetProperty("project_id", out var projectId) && projectId.ValueKind == JsonValueKind.Array
            && projectId.GetArrayLength() > 1 && projectId[1].GetString() is { } projectName)
            project = new ClientProject(projectName, codes.GetValueOrDefault(projectId[0].GetInt32()));
        var emails = new[] { Text(row, "email"), Text(row, "alternate_email") }.OfType<string>().ToList();
        var phones = new[] { Text(row, "phone") }.OfType<string>().ToList();

        return new ContactCandidate(
            row.GetProperty("id").GetInt32(),
            Text(row, "name") ?? "",
            Text(row, "company_name"),
            project,
            emails,
            phones);
    }

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
