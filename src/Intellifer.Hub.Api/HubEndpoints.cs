using System.Text.Json;
using Intellifer.Hub.Application;
using Intellifer.Hub.Application.Options;
using Intellifer.Hub.Domain;
using Microsoft.Extensions.Options;

namespace Intellifer.Hub.Api;

public static class HubEndpoints
{
    public static void MapHubEndpoints(this WebApplication app)
    {
        app.MapGet("/", () => Results.Ok(new
        {
            name = "Intellifer Hub",
            receive = new[] { "openwa", "gmail" }
        }));

        app.MapPost("/webhooks/openwa", ReceiveOpenWa);

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
