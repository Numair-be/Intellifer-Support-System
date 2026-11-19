namespace Intellifer.Hub.Domain.Entities;

public sealed class ProcessedMessage
{
    public ChannelType Channel { get; set; }

    public string ExternalMessageId { get; set; } = "";

    public DateTimeOffset ProcessedAt { get; set; }
}
