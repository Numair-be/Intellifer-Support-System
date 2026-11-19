namespace Intellifer.Hub.Application.Options;

public sealed class HubOptions
{
    public const string Section = "Hub";

    public string? ApiKey { get; set; }

    public string DefaultPhoneRegion { get; set; } = "IN";
}
