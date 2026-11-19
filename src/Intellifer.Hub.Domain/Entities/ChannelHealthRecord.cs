namespace Intellifer.Hub.Domain.Entities;

public sealed class ChannelHealthRecord
{
    public ChannelType Channel { get; set; }

    public string Status { get; set; } = "";

    public string? Detail { get; set; }

    public DateTimeOffset CheckedAt { get; set; }
}
