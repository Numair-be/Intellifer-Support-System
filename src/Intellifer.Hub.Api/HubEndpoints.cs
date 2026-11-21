using System.Text.Json;
using Intellifer.Hub.Application;
using Intellifer.Hub.Application.Options;
using Intellifer.Hub.Domain;
using Microsoft.Extensions.Options;

namespace Intellifer.Hub.Api;

public static partial class HubEndpoints
{
    public static void MapHubEndpoints(this WebApplication app)
    {
        app.MapGet("/", () => Results.Ok(new
        {
            name = "Intellifer Hub",
            receive = new[] { "openwa", "gmail" }
        }));

        app.MapPost("/webhooks/openwa", ReceiveOpenWa);

        // Apps on the server report errors here with their own key (X-App-Key), not the hub's admin key.
        app.MapPost("/api/errors", ReceiveAppError);

        app.MapGet("/health/openwa", async (IOpenWaSessionProbe probe, IChannelHealthStore health, CancellationToken cancellationToken) =>
        {
            var live = await probe.ProbeAsync(cancellationToken);
            var lastWebhook = await health.GetAsync(ChannelType.WhatsApp, cancellationToken);
            var sessionNeedsAttention = lastWebhook?.Status is "attention" or "error";
            var gatewayDown = live.Configured && (!live.Reachable || live.Connected == false || live.Ready == false);
            var statusCode = gatewayDown || sessionNeedsAttention
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status200OK;
            return Results.Json(new { live, lastWebhook }, statusCode: statusCode);
        });

        var api = app.MapGroup("/api").AddEndpointFilter<ApiKeyFilter>();
        api.MapGet("/channels/health", async (IChannelHealthStore health, CancellationToken cancellationToken) =>
            Results.Ok(await health.ListAsync(cancellationToken)));

        // Manual check of the Odoo lookup: /api/customers/lookup?channel=Email&sender=someone@example.com
        api.MapGet("/customers/lookup", async (
            ChannelType channel,
            string sender,
            ISenderNormalizer normalizer,
            ICustomerDirectory customers,
            CancellationToken cancellationToken) =>
        {
            if (!customers.IsConfigured)
                return Results.Json(new { error = "Odoo is not configured." }, statusCode: StatusCodes.Status503ServiceUnavailable);

            var normalized = normalizer.Normalize(channel, sender);
            if (!CustomerKey.TryCreate(channel, normalized, out var key))
                return Results.BadRequest(new { error = $"'{normalized}' is not an email or phone number to look up." });

            try
            {
                var match = await customers.FindAsync(key, cancellationToken);
                return Results.Ok(new { lookup = key, match });
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return Results.Json(new { lookup = key, error = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Shows how Odoo client projects link to Plane projects, without creating anything.
        api.MapGet("/plane/check", async (
            IPlaneGateway plane,
            PlaneProjectResolver resolver,
            ICustomerDirectory customers,
            IOptions<PlaneOptions> planeOptions,
            CancellationToken cancellationToken) =>
        {
            if (!plane.IsConfigured)
                return Results.Json(
                    new { error = "Plane is not configured. Set PLANE_URL, PLANE_API_KEY and PLANE_WORKSPACE_SLUG." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            try
            {
                var planeProjects = await resolver.GetProjectsAsync(cancellationToken);
                var clientProjects = await customers.ListProjectsAsync(cancellationToken);
                var overrides = PlaneProjectResolver.ParseMap(planeOptions.Value.ProjectMap);
                var links = clientProjects.Select(client =>
                {
                    var target = PlaneProjectResolver.Find(client, planeProjects, overrides);
                    return new
                    {
                        odooProject = client.ToString(),
                        planeProject = target is null ? null : $"{target.Name} ({target.Identifier})",
                        intakeEnabled = target?.IntakeEnabled,
                        status = target is null ? "no matching Plane project: messages are skipped"
                            : !target.IntakeEnabled ? "Intake is off in Plane: messages are skipped"
                            : "linked"
                    };
                });
                return Results.Ok(new
                {
                    workspace = planeOptions.Value.WorkspaceSlug,
                    links,
                    planeProjects = planeProjects.Select(project => new { project.Name, project.Identifier, project.IntakeEnabled })
                });
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // WhatsApp: see the session, link it (pairing code or QR), or unlink it.
        api.MapGet("/whatsapp", async (IWhatsAppGateway whatsApp, CancellationToken cancellationToken) =>
            await WhatsAppCallAsync(whatsApp, async () => Results.Ok(await whatsApp.EnsureSetupAsync(cancellationToken))));

        // POST /api/whatsapp/link            pairing code for WA_LINK_PHONE (or a QR code if it is empty)
        // POST /api/whatsapp/link?phone=91…  pairing code for that number
        // POST /api/whatsapp/link?qr=true    QR code
        api.MapPost("/whatsapp/link", async (
            string? phone,
            bool? qr,
            IWhatsAppGateway whatsApp,
            IOptions<OpenWaOptions> openWa,
            CancellationToken cancellationToken) =>
            await WhatsAppCallAsync(whatsApp, async () =>
            {
                var number = qr == true ? null : phone ?? openWa.Value.LinkPhone;
                var result = await whatsApp.LinkAsync(number, cancellationToken);
                return Results.Ok(new
                {
                    result.Status,
                    result.PairingCode,
                    result.QrCode,
                    next = result.PairingCode is not null
                        ? "On the phone: WhatsApp > Settings > Linked devices > Link a device > Link with phone number instead, then enter the pairing code."
                        : result.QrCode is not null
                            ? "On the phone: WhatsApp > Settings > Linked devices > Link a device, then scan the QR (also shown in the OpenWA dashboard)."
                            : "Already linked."
                });
            }));

        api.MapPost("/whatsapp/logout", async (IWhatsAppGateway whatsApp, CancellationToken cancellationToken) =>
            await WhatsAppCallAsync(whatsApp, async () =>
            {
                await whatsApp.LogoutAsync(cancellationToken);
                return Results.Ok(new { status = "logged out", next = "Link again with POST /api/whatsapp/link." });
            }));

        // Projects page: projects (same names as in Plane) and the apps connected to them. Employees come from Odoo.
        api.MapGet("/projects", async (IProjectAdmin projects, IAppRegistry apps, PlaneProjectResolver plane, CancellationToken cancellationToken) =>
        {
            var list = await projects.ListAsync(cancellationToken);
            var allApps = await apps.ListAsync(cancellationToken);
            var planeProjects = await TryPlaneProjectsAsync(plane, cancellationToken);
            return Results.Ok(list.Select(project => new
            {
                project.Id,
                project.Name,
                project.Notes,
                AppCount = allApps.Count(app => SameProject(app.Project, project.Name)),
                Plane = PlaneLink(project.Name, planeProjects),
                project.UpdatedAt
            }));
        });

        api.MapGet("/projects/{id:guid}", async (Guid id, IProjectAdmin projects, IAppRegistry apps, PlaneProjectResolver plane, CancellationToken cancellationToken) =>
        {
            var project = await projects.GetAsync(id, cancellationToken);
            if (project is null)
                return Results.NotFound();
            var connected = (await apps.ListAsync(cancellationToken)).Where(app => SameProject(app.Project, project.Name)).ToList();
            return Results.Ok(new
            {
                project.Id,
                project.Name,
                project.Notes,
                project.CreatedAt,
                project.UpdatedAt,
                Apps = connected,
                Plane = PlaneLink(project.Name, await TryPlaneProjectsAsync(plane, cancellationToken))
            });
        });

        api.MapPost("/projects", async (ProjectRequest request, IProjectAdmin projects, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "Name is required: the same name as the project in Plane." });
            var (result, id) = await projects.CreateAsync(request.Name, request.Notes, cancellationToken);
            return result == ProjectSaveResult.NameTaken
                ? Results.Conflict(new { error = $"A project called '{request.Name.Trim()}' already exists." })
                : Results.Created($"/api/projects/{id}", new { id });
        });

        api.MapPut("/projects/{id:guid}", async (Guid id, ProjectRequest request, IProjectAdmin projects, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "Name is required: the same name as the project in Plane." });
            return await projects.UpdateAsync(id, request.Name, request.Notes, cancellationToken) switch
            {
                ProjectSaveResult.NotFound => Results.NotFound(),
                ProjectSaveResult.NameTaken => Results.Conflict(new { error = $"A project called '{request.Name.Trim()}' already exists." }),
                _ => Results.NoContent()
            };
        });

        api.MapDelete("/projects/{id:guid}", async (Guid id, IProjectAdmin projects, CancellationToken cancellationToken) =>
            await projects.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

        // Who would be told about a new intake item in this project: GET /api/team?project=Support Test
        api.MapGet("/team", async (string? project, IProjectTeamDirectory team, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "Add ?project=<project name>." });
            try
            {
                var members = await team.GetTeamAsync([project], cancellationToken);
                return Results.Ok(new
                {
                    project,
                    members,
                    note = members.Count == 0
                        ? "Nobody to notify: assign employees with a work mobile, phone or email to the project in Odoo."
                        : null
                });
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Apps that may report errors, and the project each belongs to (until this moves to Odoo).
        api.MapGet("/apps", async (IAppRegistry apps, CancellationToken cancellationToken) =>
            Results.Ok(await apps.ListAsync(cancellationToken)));

        api.MapPost("/apps", async (NewAppRequest request, IAppRegistry apps, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || !AppNamePattern().IsMatch(request.Name.Trim()))
                return Results.BadRequest(new { error = "name: 2-100 letters, digits, dot, dash or underscore, e.g. crm-backend." });
            if (string.IsNullOrWhiteSpace(request.Project))
                return Results.BadRequest(new { error = "project: the project name or code its errors belong to, e.g. Support Test." });

            var key = AppKeys.NewKey();
            var created = await apps.CreateAsync(request.Name, request.Project, AppKeys.Hash(key), AppKeys.Hint(key), cancellationToken);
            return created is null
                ? Results.Conflict(new { error = $"An app called '{request.Name.Trim()}' already exists." })
                : Results.Created($"/api/apps/{created.Id}", new
                {
                    app = created,
                    key,
                    note = "Store this key in the app now: it is shown only once. The app sends it as the X-App-Key header to POST /api/errors."
                });
        });

        api.MapPatch("/apps/{id:guid}", async (Guid id, UpdateAppRequest request, IAppRegistry apps, CancellationToken cancellationToken) =>
            await apps.UpdateAsync(id, request.Project, request.Active, cancellationToken) is { } updated
                ? Results.Ok(updated)
                : Results.NotFound());

        api.MapPost("/apps/{id:guid}/key", async (Guid id, IAppRegistry apps, CancellationToken cancellationToken) =>
        {
            var key = AppKeys.NewKey();
            return await apps.ReplaceKeyAsync(id, AppKeys.Hash(key), AppKeys.Hint(key), cancellationToken) is { } updated
                ? Results.Ok(new { app = updated, key, note = "The old key stops working now. This key is shown only once." })
                : Results.NotFound();
        });

        api.MapDelete("/apps/{id:guid}", async (Guid id, IAppRegistry apps, CancellationToken cancellationToken) =>
            await apps.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

        api.MapGet("/conversations", async (int? take, IInboundQuery query, CancellationToken cancellationToken) =>
            Results.Ok(await query.ListConversationsAsync(Clamp(take, 50, 100), cancellationToken)));

        api.MapGet("/conversations/{id:guid}/messages", async (
            Guid id,
            int? take,
            IInboundQuery query,
            CancellationToken cancellationToken) =>
        {
            var messages = await query.ListMessagesAsync(id, Clamp(take, 100, 200), cancellationToken);
            return messages is null ? Results.NotFound() : Results.Ok(messages);
        });
    }

    private static async Task<IResult> ReceiveOpenWa(
        HttpContext context,
        IOptions<OpenWaOptions> options,
        OpenWaEventInterpreter interpreter,
        IInboundIntake intake,
        IChannelHealthStore health,
        ILogger<OpenWaEventInterpreter> logger,
        CancellationToken cancellationToken)
    {
        var expected = options.Value.WebhookSecret;
        if (string.IsNullOrWhiteSpace(expected))
            return Results.Json(new { error = "OpenWA webhook secret is not configured." }, statusCode: StatusCodes.Status503ServiceUnavailable);

        if (!context.Request.Headers.TryGetValue("X-Webhook-Secret", out var provided)
            || !SecretComparer.Equals(provided.ToString(), expected))
            return Results.Unauthorized();

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        using (document)
        {
            var interpretation = interpreter.Interpret(document.RootElement);
            switch (interpretation)
            {
                case OpenWaInterpretation.Inbound inbound:
                    var correlationId = context.Items["CorrelationId"] as string;
                    var message = string.IsNullOrWhiteSpace(correlationId)
                        ? inbound.Message
                        : inbound.Message with { CorrelationId = correlationId };
                    await intake.AcceptAsync(message, cancellationToken);
                    break;
                case OpenWaInterpretation.Session session:
                    await health.UpsertAsync(ChannelType.WhatsApp, session.Status, session.Detail, cancellationToken);
                    logger.LogInformation("WhatsApp session status is {Status}", session.Status);
                    break;
                case OpenWaInterpretation.Ignored ignored:
                    logger.LogDebug("Ignored OpenWA event: {Reason}", ignored.Reason);
                    break;
            }
        }

        return Results.NoContent();
    }

    /// <summary>
    /// An error from an app: JSON {"message", "source"?, "level"?, "occurredAt"?} or the error as plain text.
    /// Answers 202 straight away; the Plane intake item is created by the worker.
    /// </summary>
    private static async Task<IResult> ReceiveAppError(
        HttpContext context,
        IAppRegistry apps,
        AppErrorIntake intake,
        CancellationToken cancellationToken)
    {
        var key = context.Request.Headers["X-App-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key))
            return Results.Json(new { error = "Send the app's key in the X-App-Key header." }, statusCode: StatusCodes.Status401Unauthorized);
        var app = await apps.FindByKeyHashAsync(AppKeys.Hash(key), cancellationToken);
        if (app is null)
            return Results.Json(new { error = "Unknown app key." }, statusCode: StatusCodes.Status401Unauthorized);
        if (!app.Active)
            return Results.Json(new { error = $"App {app.Name} is disabled." }, statusCode: StatusCodes.Status403Forbidden);

        AppErrorReport report;
        if (context.Request.HasJsonContentType())
        {
            AppErrorRequest? body;
            try
            {
                body = await context.Request.ReadFromJsonAsync<AppErrorRequest>(cancellationToken);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "The body is not valid JSON." });
            }

            report = new AppErrorReport(body?.Message ?? "", body?.Source, body?.Level, body?.OccurredAt);
        }
        else
        {
            using var reader = new StreamReader(context.Request.Body);
            report = new AppErrorReport(await reader.ReadToEndAsync(cancellationToken), null, null, null);
        }

        if (string.IsNullOrWhiteSpace(report.Message))
            return Results.BadRequest(new { error = "message is empty." });
        if (report.Message.Length > AppErrorIntake.MaxMessageLength)
            return Results.Json(new { error = $"message is longer than {AppErrorIntake.MaxMessageLength} characters." }, statusCode: StatusCodes.Status413PayloadTooLarge);

        var accepted = await intake.AcceptAsync(app, report, cancellationToken);
        return Results.Json(new
        {
            id = accepted.ErrorId,
            app = app.Name,
            project = app.Project,
            accepted.Repeat,
            accepted.Occurrences,
            accepted.Status
        }, statusCode: StatusCodes.Status202Accepted);
    }

    public sealed record ProjectRequest(string? Name, string? Notes);

    private static bool SameProject(string a, string b) =>
        PlaneProjectResolver.Normalize(a) == PlaneProjectResolver.Normalize(b);

    private static async Task<IReadOnlyList<PlaneProject>?> TryPlaneProjectsAsync(PlaneProjectResolver plane, CancellationToken cancellationToken)
    {
        try
        {
            return await plane.GetProjectsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Whether a Plane project with this name exists, and whether intake items can be created in it.</summary>
    private static object PlaneLink(string name, IReadOnlyList<PlaneProject>? planeProjects)
    {
        if (planeProjects is null)
            return new { status = "unknown", detail = "Plane could not be reached." };
        var match = PlaneProjectResolver.Find(new ClientProject(name, name), planeProjects, new Dictionary<string, string>());
        if (match is null)
            return new { status = "missing", detail = "No Plane project has this name. Use the exact Plane project name." };
        return match.IntakeEnabled
            ? new { status = "linked", detail = $"{match.Name} ({match.Identifier})" }
            : new { status = "intake-off", detail = $"{match.Name} ({match.Identifier}): turn on Intake in Plane." };
    }

    public sealed record AppErrorRequest(string? Message, string? Source, string? Level, DateTimeOffset? OccurredAt);

    public sealed record NewAppRequest(string? Name, string? Project);

    public sealed record UpdateAppRequest(string? Project, bool? Active);

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9._-]{2,100}$")]
    private static partial System.Text.RegularExpressions.Regex AppNamePattern();

    private static async Task<IResult> WhatsAppCallAsync(IWhatsAppGateway whatsApp, Func<Task<IResult>> call)
    {
        if (!whatsApp.IsConfigured)
            return Results.Json(new { error = "The WhatsApp gateway is not configured (OPENWA_URL, WA_API_KEY)." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        try
        {
            return await call();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static int Clamp(int? take, int fallback, int max)
    {
        var value = take.GetValueOrDefault(fallback);
        if (value < 1)
            return 1;
        return value > max ? max : value;
    }
}
