namespace Intellifer.Hub.Application;

public interface IGmailSyncStateStore
{
    Task<string?> GetHistoryIdAsync(string mailbox, CancellationToken cancellationToken);

    Task SetHistoryIdAsync(string mailbox, string historyId, CancellationToken cancellationToken);
}
