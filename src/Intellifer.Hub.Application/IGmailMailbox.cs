namespace Intellifer.Hub.Application;

public sealed record GmailProfile(string EmailAddress, string HistoryId);

public sealed record GmailHistoryPage(string? LatestHistoryId, IReadOnlyList<string> AddedMessageIds);

public sealed record GmailFetchedMessage(
    string Id,
    string ThreadId,
    string? FromHeader,
    string? Subject,
    string Body,
    bool BodyWasHtml,
    IReadOnlyList<string> LabelIds,
    IReadOnlyList<string> AttachmentNames,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<GmailImagePart>? Images = null);

/// <summary>An image part of an email: its data inline, or an attachment id to download it.</summary>
/// <param name="IsInline">Shown inside the email body (pasted screenshot or signature logo) rather than attached.</param>
public sealed record GmailImagePart(string FileName, string MimeType, long Size, string? AttachmentId, byte[]? InlineData, bool IsInline = false);

public sealed class GmailHistoryExpiredException : Exception
{
    public GmailHistoryExpiredException()
        : base("The stored Gmail history id is no longer valid.")
    {
    }
}

public interface IGmailMailbox
{
    bool IsConfigured { get; }

    Task<GmailProfile> GetProfileAsync(CancellationToken cancellationToken);

    Task<GmailHistoryPage> ListHistoryAsync(string startHistoryId, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListRecentMessageIdsAsync(int newerThanDays, int maxMessages, CancellationToken cancellationToken);

    Task<GmailFetchedMessage?> GetMessageAsync(string messageId, CancellationToken cancellationToken);

    Task ApplyProcessedLabelAsync(string messageId, CancellationToken cancellationToken);

    Task<byte[]> GetAttachmentAsync(string messageId, string attachmentId, CancellationToken cancellationToken);
}
