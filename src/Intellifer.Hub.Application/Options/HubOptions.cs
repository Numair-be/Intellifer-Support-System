namespace Intellifer.Hub.Application.Options;

public sealed class HubOptions
{
    public const string Section = "Hub";

    public string? ApiKey { get; set; }

    public string DefaultPhoneRegion { get; set; } = "IN";

    /// <summary>Time zone for dates shown in tickets, e.g. Asia/Kolkata.</summary>
    public string TimeZone { get; set; } = "Asia/Kolkata";

    /// <summary>The same error from the same app within this many minutes is counted, not ticketed again.</summary>
    public int AppErrorRepeatMinutes { get; set; } = 60;

    /// <summary>At most this many new tickets per app per hour; more are stored and skipped.</summary>
    public int AppErrorMaxTicketsPerHour { get; set; } = 30;
}
