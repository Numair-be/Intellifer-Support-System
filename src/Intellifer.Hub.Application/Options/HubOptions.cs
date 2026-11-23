namespace Intellifer.Hub.Application.Options;

public sealed class HubOptions
{
    public const string Section = "Hub";

    public string? ApiKey { get; set; }

    public string DefaultPhoneRegion { get; set; } = "IN";

    /// <summary>Time zone for dates shown in tickets, e.g. Asia/Kolkata.</summary>
    public string TimeZone { get; set; } = "Asia/Kolkata";

    /// <summary>After an app's first error for a project, wait this long for more; all of them become one ticket.</summary>
    public int AppErrorBatchSeconds { get; set; } = 60;

    /// <summary>The same error of a project within this many minutes after its ticket is counted, not ticketed again.</summary>
    public int AppErrorRepeatMinutes { get; set; } = 60;

    /// <summary>At most this many error tickets per project per hour; more errors are stored and skipped.</summary>
    public int AppErrorMaxTicketsPerHour { get; set; } = 30;

    /// <summary>Optional shared key apps must send as X-App-Key to POST /api/errors. Empty: no key needed.</summary>
    public string? AppErrorKey { get; set; }
}
