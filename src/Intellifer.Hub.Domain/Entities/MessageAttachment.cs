namespace Intellifer.Hub.Domain.Entities;

public enum AttachmentState
{
    /// <summary>Known, but the file still has to be fetched from the channel (WhatsApp media).</summary>
    PendingDownload = 1,

    /// <summary>File held in the hub, waiting for its message to reach a Plane ticket.</summary>
    Ready = 2,

    /// <summary>Attached to the Plane ticket. The file itself is no longer kept.</summary>
    Uploaded = 3,

    /// <summary>Gave up after the maximum number of attempts.</summary>
    Failed = 4,

    /// <summary>Not carried over on purpose, e.g. too large. The reason is in <see cref="MessageAttachment.Error"/>.</summary>
    Skipped = 5
}

/// <summary>An image that came with an inbound message and is carried over to the Plane ticket.</summary>
public sealed class MessageAttachment
{
    public Guid Id { get; set; }

    public Guid MessageId { get; set; }

    public HubMessage Message { get; set; } = null!;

    public string FileName { get; set; } = "";

    public string ContentType { get; set; } = "";

    public long Size { get; set; }

    /// <summary>The file, until it has been uploaded to Plane.</summary>
    public byte[]? Data { get; set; }

    /// <summary>What the channel needs to fetch the file later (the WhatsApp message JSON).</summary>
    public string? Source { get; set; }

    public AttachmentState State { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    public string? Error { get; set; }

    public Guid? PlaneAttachmentId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UploadedAt { get; set; }
}
