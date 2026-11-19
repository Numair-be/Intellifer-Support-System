using Intellifer.Hub.Domain;

namespace Intellifer.Hub.Application;

/// <summary>An image whose file still has to be fetched from its channel.</summary>
public sealed record AttachmentDownload(Guid Id, ChannelType Channel, string Source, string ContentType, int Attempts);

/// <summary>An image ready to go to the Plane ticket of its conversation.</summary>
public sealed record AttachmentUpload(
    Guid Id,
    string FileName,
    string ContentType,
    byte[] Data,
    Guid ProjectId,
    Guid WorkItemId,
    int Attempts);

public interface IAttachmentStore
{
    /// <summary>Images to fetch, only for messages that reached a Plane ticket.</summary>
    Task<IReadOnlyList<AttachmentDownload>> GetDownloadsDueAsync(int take, DateTimeOffset now, CancellationToken cancellationToken);

    Task SaveDownloadedAsync(Guid id, byte[] data, string contentType, CancellationToken cancellationToken);

    /// <summary>Images whose message already reached a Plane ticket.</summary>
    Task<IReadOnlyList<AttachmentUpload>> GetUploadsDueAsync(
        int take,
        DateTimeOffset now,
        Guid? defaultProjectId,
        CancellationToken cancellationToken);

    /// <summary>Records the Plane attachment and drops the file from the hub.</summary>
    Task MarkUploadedAsync(Guid id, Guid planeAttachmentId, DateTimeOffset now, CancellationToken cancellationToken);

    Task MarkFailedAsync(Guid id, string error, DateTimeOffset? retryAt, CancellationToken cancellationToken);

    Task MarkSkippedAsync(Guid id, string reason, CancellationToken cancellationToken);
}

/// <summary>Fetches WhatsApp media from the gateway.</summary>
public interface IWhatsAppMedia
{
    bool IsConfigured { get; }

    Task<(byte[] Data, string ContentType)> DownloadAsync(string messageJson, CancellationToken cancellationToken);
}
