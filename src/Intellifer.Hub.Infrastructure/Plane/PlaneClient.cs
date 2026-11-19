using System.Text.Json;
using System.Text.Json.Nodes;
using Intellifer.Hub.Application;
using Intellifer.Hub.Application.Options;
using Microsoft.Extensions.Options;

namespace Intellifer.Hub.Infrastructure.Plane;

public sealed class PlaneApiException(string message) : Exception(message);

/// <summary>Plane REST API v1 with an API key (X-API-Key).</summary>
public sealed class PlaneClient(HttpClient http, IOptions<PlaneOptions> options) : IPlaneGateway
{
    public bool IsConfigured => options.Value.IsConfigured;

    public async Task<IReadOnlyList<PlaneProject>> ListProjectsAsync(CancellationToken cancellationToken)
    {
        var projects = new List<PlaneProject>();
        string? cursor = null;
        // Plane pages with a cursor; a workspace rarely has more than one page of 100.
        for (var page = 0; page < 20; page++)
        {
            var url = WorkspacePath("projects/?per_page=100" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            using var document = await SendAsync(HttpMethod.Get, url, null, cancellationToken);
            var root = document.RootElement;
            var rows = root.ValueKind == JsonValueKind.Array
                ? root
                : root.TryGetProperty("results", out var results) ? results : default;
            if (rows.ValueKind == JsonValueKind.Array)
                projects.AddRange(rows.EnumerateArray().Select(ToProject).OfType<PlaneProject>());

            var more = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("next_page_results", out var next) && next.ValueKind == JsonValueKind.True;
            cursor = more ? ReadString(root, "next_cursor") : null;
            if (cursor is null)
                break;
        }

        return projects;
    }

    private static PlaneProject? ToProject(JsonElement row)
    {
        if (ReadGuid(row, "id") is not { } id)
            return null;
        // Archived projects cannot take new work.
        if (row.TryGetProperty("archived_at", out var archived) && archived.ValueKind == JsonValueKind.String)
            return null;

        var intake = false;
        foreach (var name in new[] { "intake_view", "inbox_view" })
        {
            if (row.TryGetProperty(name, out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                intake = flag.GetBoolean();
                break;
            }
        }

        return new PlaneProject(id, ReadString(row, "name") ?? "", ReadString(row, "identifier") ?? "", intake);
    }

    public async Task<PlaneIntakeCreated> CreateIntakeAsync(Guid projectId, string title, string descriptionHtml, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["issue"] = new JsonObject
            {
                ["name"] = title,
                ["description_html"] = descriptionHtml,
                ["priority"] = "none"
            }
        };

        using var document = await SendAsync(HttpMethod.Post, ProjectPath(projectId, "intake-issues/"), body, cancellationToken);
        var root = document.RootElement;
        var intakeId = ReadGuid(root, "id")
            ?? throw new PlaneApiException("Plane created the intake item but returned no id.");

        // The work item comes back either as an object { "id": ... } or as a bare id, depending on the version.
        Guid? workItemId = null;
        int? sequenceId = null;
        if (root.TryGetProperty("issue", out var issue))
        {
            if (issue.ValueKind == JsonValueKind.Object)
            {
                workItemId = ReadGuid(issue, "id");
                if (issue.TryGetProperty("sequence_id", out var sequence) && sequence.ValueKind == JsonValueKind.Number)
                    sequenceId = sequence.GetInt32();
            }
            else if (issue.ValueKind == JsonValueKind.String && Guid.TryParse(issue.GetString(), out var id))
            {
                workItemId = id;
            }
        }

        if (workItemId is null)
            throw new PlaneApiException("Plane created the intake item but returned no work item id.");

        // Some versions return the work item as a bare id. The intake record (looked up by work item id)
        // has the number shown in Plane, e.g. 8 in PLANE-8. Nice to have, so a failure here is ignored.
        sequenceId ??= await TryGetSequenceIdAsync(projectId, workItemId.Value, cancellationToken);
        return new PlaneIntakeCreated(intakeId, workItemId.Value, sequenceId);
    }

    private async Task<int?> TryGetSequenceIdAsync(Guid projectId, Guid workItemId, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await SendAsync(HttpMethod.Get, ProjectPath(projectId, $"intake-issues/{workItemId}/"), null, cancellationToken);
            return document.RootElement.TryGetProperty("issue_detail", out var detail)
                && detail.ValueKind == JsonValueKind.Object
                && detail.TryGetProperty("sequence_id", out var sequence)
                && sequence.ValueKind == JsonValueKind.Number
                ? sequence.GetInt32()
                : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task AddCommentAsync(Guid projectId, Guid workItemId, string commentHtml, string externalId, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["comment_html"] = commentHtml,
            ["access"] = "INTERNAL",
            ["external_source"] = "intellifer-hub",
            ["external_id"] = externalId
        };
        using var _ = await SendAsync(HttpMethod.Post, ProjectPath(projectId, $"work-items/{workItemId}/comments/"), body, cancellationToken);
    }

    public async Task<PlaneItemStatus> GetIntakeStatusAsync(Guid projectId, Guid workItemId, CancellationToken cancellationToken)
    {
        // This Plane looks intake records up by the work item id and returns the item as issue_detail.
        using var document = await SendAsync(HttpMethod.Get, ProjectPath(projectId, $"intake-issues/{workItemId}/"), null, cancellationToken);
        var root = document.RootElement;
        int? intake = root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number
            ? status.GetInt32()
            : null;

        string? stateName = null, stateGroup = null;
        if (root.TryGetProperty("issue_detail", out var detail) && detail.ValueKind == JsonValueKind.Object
            && detail.TryGetProperty("state", out var state))
        {
            if (state.ValueKind == JsonValueKind.Object)
            {
                stateName = ReadString(state, "name");
                stateGroup = ReadString(state, "group");
            }
            else if (state.ValueKind == JsonValueKind.String && Guid.TryParse(state.GetString(), out var stateId))
            {
                (stateName, stateGroup) = await GetStateAsync(projectId, stateId, cancellationToken);
            }
        }

        return new PlaneItemStatus(intake, stateName, stateGroup);
    }

    private async Task<(string? Name, string? Group)> GetStateAsync(Guid projectId, Guid stateId, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, ProjectPath(projectId, "states/"), null, cancellationToken);
        var root = document.RootElement;
        var rows = root.ValueKind == JsonValueKind.Array ? root : root.TryGetProperty("results", out var results) ? results : default;
        if (rows.ValueKind != JsonValueKind.Array)
            return (null, null);
        foreach (var row in rows.EnumerateArray())
        {
            if (ReadGuid(row, "id") == stateId)
                return (ReadString(row, "name"), ReadString(row, "group"));
        }

        return (null, null);
    }

    public async Task<Guid> UploadAttachmentAsync(
        Guid projectId,
        Guid workItemId,
        string fileName,
        string contentType,
        byte[] data,
        CancellationToken cancellationToken)
    {
        var path = ProjectPath(projectId, $"work-items/{workItemId}/attachments/");

        // 1. Ask Plane for a signed upload to its storage (MinIO on this server, S3 on Plane Cloud).
        var request = new JsonObject { ["name"] = fileName, ["type"] = contentType, ["size"] = data.LongLength };
        Guid attachmentId;
        string uploadUrl;
        var fields = new List<KeyValuePair<string, string>>();
        using (var document = await SendAsync(HttpMethod.Post, path, request, cancellationToken))
        {
            var root = document.RootElement;
            attachmentId = ReadGuid(root, "asset_id")
                ?? (root.TryGetProperty("attachment", out var attachment) ? ReadGuid(attachment, "id") : null)
                ?? throw new PlaneApiException("Plane returned no attachment id for the upload.");
            if (!root.TryGetProperty("upload_data", out var upload) || ReadString(upload, "url") is not { } url)
                throw new PlaneApiException("Plane returned no upload_data.url for the attachment.");
            uploadUrl = url.StartsWith('/') ? options.Value.Url!.TrimEnd('/') + url : url;
            if (upload.TryGetProperty("fields", out var form) && form.ValueKind == JsonValueKind.Object)
                fields.AddRange(form.EnumerateObject().Select(field => KeyValuePair.Create(field.Name, field.Value.GetString() ?? "")));
        }

        // 2. Send the file to storage: the signed fields first, the file last (S3 POST policy rules).
        using (var form = new MultipartFormDataContent("----intellifer" + Guid.NewGuid().ToString("n")))
        {
            foreach (var (name, value) in fields)
            {
                var part = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(value));
                form.Add(part, $"\"{name}\"");
            }

            var file = new ByteArrayContent(data);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            form.Add(file, "\"file\"", $"\"{fileName.Replace("\"", "")}\"");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            // Buffer so Content-Length is set; storage behind the proxy rejects chunked uploads.
            await form.LoadIntoBufferAsync(timeout.Token);
            using var response = await http.PostAsync(uploadUrl, form, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new PlaneApiException(
                    $"Plane storage refused the file: {(int)response.StatusCode} {TextClip.Clip(await response.Content.ReadAsStringAsync(timeout.Token), 300)}");
        }

        // 3. Tell Plane the upload is complete, so the attachment shows on the ticket.
        using (await SendAsync(HttpMethod.Patch, $"{path}{attachmentId}/", new JsonObject { ["is_uploaded"] = true }, cancellationToken))
        {
        }

        return attachmentId;
    }

    private string WorkspacePath(string suffix)
    {
        var settings = options.Value;
        return $"{settings.Url!.TrimEnd('/')}/api/v1/workspaces/{Uri.EscapeDataString(settings.WorkspaceSlug!.Trim())}/{suffix}";
    }

    private string ProjectPath(Guid projectId, string suffix) => WorkspacePath($"projects/{projectId}/{suffix}");

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 3, 120)));

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-API-Key", settings.ApiKey);
        // A plain string body carries Content-Length. JsonContent is sent chunked, and the proxy in front of
        // Plane passed that on as an empty body ("Name is required").
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, timeout.Token);
        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var hint = (int)response.StatusCode switch
            {
                401 or 403 => " Check PLANE_API_KEY and that its user is a member of the project.",
                404 => " Check PLANE_WORKSPACE_SLUG, and that Intake is turned on for the project.",
                429 => " Plane's rate limit (60 requests a minute) was hit.",
                _ => ""
            };
            throw new PlaneApiException(
                $"Plane returned {(int)response.StatusCode} {response.ReasonPhrase}.{hint} {TextClip.Clip(text, 300)}".Trim());
        }

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private static Guid? ReadGuid(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && Guid.TryParse(value.GetString(), out var id)
            ? id
            : null;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
