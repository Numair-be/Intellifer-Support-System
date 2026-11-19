namespace Intellifer.Hub.Domain.Entities;

public sealed class GmailSyncState
{
    public string Mailbox { get; set; } = "";

    public string HistoryId { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }
}
