namespace Intellifer.Hub.Application.Options;

public sealed class PlaneOptions
{
    public const string Section = "Plane";

    public const string DefaultKeywords =
        "bug,bugs,error,errors,issue,issues,problem,not working,doesn't work,does not work,isn't working," +
        "is not working,broken,crash,crashes,crashed,crashing,exception,failed,failing,failure,glitch,unable to,fix";

    /// <summary>Base URL of the self-hosted Plane, e.g. https://plane.example.com. The API lives under /api/v1.</summary>
    public string? Url { get; set; }

    /// <summary>Workspace settings → API tokens. Sent as X-API-Key.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The workspace part of the Plane URL: https://plane.example.com/{slug}/projects/...</summary>
    public string? WorkspaceSlug { get; set; }

    /// <summary>
    /// Optional. The project for conversations opened before per-project routing, which did not record one.
    /// New conversations go to the project that matches the sender's Odoo client project.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Optional overrides for when Odoo and Plane names differ: "Odoo project=Plane name or identifier", comma-separated.
    /// </summary>
    public string? ProjectMap { get; set; }

    /// <summary>How long the list of Plane projects is cached. New Plane projects are picked up after this.</summary>
    public int ProjectCacheMinutes { get; set; } = 5;

    /// <summary>Comma-separated words or phrases. A message containing any of them opens an intake item.</summary>
    public string IntakeKeywords { get; set; } = DefaultKeywords;

    public int PollIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// A new ticket waits until the conversation has been quiet this long, so a report sent in pieces
    /// ("hi", a screenshot, "upload fails") becomes one ticket. WhatsApp messages come in bursts.
    /// </summary>
    public int WhatsAppQuietSeconds { get; set; } = 120;

    /// <summary>Same for email. An email usually arrives complete, so there is no wait by default.</summary>
    public int EmailQuietSeconds { get; set; } = 0;

    /// <summary>
    /// A chat that already has a ticket gets a new one when that ticket is closed (done, cancelled, declined,
    /// duplicate) or when nothing was added to it for this long. Otherwise new messages are comments on it.
    /// </summary>
    public int NewTicketAfterIdleHours { get; set; } = 24;

    /// <summary>Messages sent per poll. Plane allows 60 requests a minute per API key.</summary>
    public int BatchSize { get; set; } = 10;

    public int MaxAttempts { get; set; } = 8;

    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Reply to the customer, on the channel they used, when their intake item changes status.</summary>
    public bool StatusReplies { get; set; } = true;

    /// <summary>Tell the customer their ticket number when an intake item is created for them.</summary>
    public bool AcknowledgeIntake { get; set; } = true;

    /// <summary>How often each open intake item's status is checked.</summary>
    public int StatusPollMinutes { get; set; } = 2;

    /// <summary>Largest image carried over to a Plane ticket. Plane's own default upload limit is 5 MB.</summary>
    public int AttachmentMaxMegabytes { get; set; } = 5;

    public long AttachmentMaxBytes => Math.Clamp(AttachmentMaxMegabytes, 1, 100) * 1024L * 1024L;

    /// <summary>Tell the employees on the client project when an intake item is created for it.</summary>
    public bool NotifyTeam { get; set; } = true;

    /// <summary>
    /// How team members are told: "whatsapp" (falls back to email when they have no phone), "email"
    /// (falls back to WhatsApp), or "both".
    /// </summary>
    public string TeamChannels { get; set; } = "whatsapp";

    /// <summary>Signs the status replies.</summary>
    public string ReplySignature { get; set; } = "Intellifer Support";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Url)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(WorkspaceSlug);

    public Guid? DefaultProjectId => Guid.TryParse(ProjectId, out var id) ? id : null;
}
