namespace Intellifer.Hub.Application;

/// <summary>Times shown to people (Plane tickets, comments) in the hub's time zone (HUB_TIME_ZONE), e.g. "2026-11-26 10:17 IST".</summary>
public static class HubTime
{
    public const string DefaultZone = "Asia/Kolkata";

    public static string Format(DateTimeOffset time, string? zoneId, string pattern = "yyyy-MM-dd HH:mm")
    {
        var zone = Zone(zoneId);
        var local = TimeZoneInfo.ConvertTime(time, zone);
        return $"{local.ToString(pattern, System.Globalization.CultureInfo.InvariantCulture)} {Label(local.Offset)}";
    }

    private static TimeZoneInfo Zone(string? zoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(zoneId) ? DefaultZone : zoneId.Trim());
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static string Label(TimeSpan offset) =>
        offset == TimeSpan.Zero ? "UTC"
        : offset == TimeSpan.FromMinutes(330) ? "IST"
        : $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}";
}
