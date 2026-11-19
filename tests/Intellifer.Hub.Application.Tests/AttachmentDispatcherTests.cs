using Intellifer.Hub.Application;
using Intellifer.Hub.Application.Options;
using Intellifer.Hub.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Intellifer.Hub.Application.Tests;

public sealed class AttachmentDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeStore _store = new();
    private readonly FakePlane _plane = new();
    private readonly FakeMedia _media = new();

    [Fact]
    public async Task Whatsapp_image_is_fetched_from_openwa()
    {
        _store.Downloads.Add(new AttachmentDownload(Guid.NewGuid(), ChannelType.WhatsApp, "{\"id\":\"img1\"}", "image/jpeg", 0));
        _media.Result = (new byte[1234], "image/jpeg");

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        var saved = Assert.Single(_store.Saved);
        Assert.Equal((1234, "image/jpeg"), (saved.Data.Length, saved.ContentType));
        Assert.Equal("{\"id\":\"img1\"}", _media.LastSource);
    }

    [Fact]
    public async Task Image_over_the_limit_is_skipped_with_the_reason()
    {
        _store.Downloads.Add(new AttachmentDownload(Guid.NewGuid(), ChannelType.WhatsApp, "{}", "image/png", 0));
        _media.Result = (new byte[2 * 1024 * 1024], "image/png");

        await Dispatcher(maxMegabytes: 1).RunOnceAsync(CancellationToken.None);

        Assert.Empty(_store.Saved);
        Assert.Contains("limit is 1 MB", Assert.Single(_store.Skipped).Reason);
    }

    [Fact]
    public async Task Ready_image_goes_to_the_ticket_of_its_conversation()
    {
        var upload = new AttachmentUpload(Guid.NewGuid(), "screenshot.png", "image/png", [1, 2, 3], Guid.NewGuid(), Guid.NewGuid(), 0);
        _store.Uploads.Add(upload);

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        var sent = Assert.Single(_plane.Uploaded);
        Assert.Equal((upload.ProjectId, upload.WorkItemId, "screenshot.png", "image/png"), (sent.ProjectId, sent.WorkItemId, sent.FileName, sent.ContentType));
        Assert.Equal(upload.Id, Assert.Single(_store.Uploaded));
    }

    [Fact]
    public async Task Openwa_down_retries_the_download_later()
    {
        _store.Downloads.Add(new AttachmentDownload(Guid.NewGuid(), ChannelType.WhatsApp, "{}", "image/png", 0));
        _media.Failure = new InvalidOperationException("OpenWA returned 503");

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        var failure = Assert.Single(_store.Failed);
        Assert.Equal(Now.AddSeconds(30), failure.RetryAt);
        Assert.StartsWith("download:", failure.Error);
    }

    [Fact]
    public async Task Upload_gives_up_after_the_last_attempt()
    {
        _store.Uploads.Add(new AttachmentUpload(Guid.NewGuid(), "a.png", "image/png", [1], Guid.NewGuid(), Guid.NewGuid(), 7));
        _plane.Failure = new HttpRequestException("Plane storage refused the file: 403");

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Null(Assert.Single(_store.Failed).RetryAt);
    }

    private AttachmentDispatcher Dispatcher(int maxMegabytes = 5) => new(
        _store,
        _plane,
        _media,
        Microsoft.Extensions.Options.Options.Create(new PlaneOptions { AttachmentMaxMegabytes = maxMegabytes }),
        new FixedClock(),
        NullLogger<AttachmentDispatcher>.Instance);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeMedia : IWhatsAppMedia
    {
        public (byte[] Data, string ContentType) Result { get; set; } = ([], "image/png");
        public Exception? Failure { get; set; }
        public string? LastSource { get; private set; }
        public bool IsConfigured => true;

        public Task<(byte[] Data, string ContentType)> DownloadAsync(string messageJson, CancellationToken cancellationToken)
        {
            LastSource = messageJson;
            return Failure is null ? Task.FromResult(Result) : Task.FromException<(byte[], string)>(Failure);
        }
    }

    private sealed class FakeStore : IAttachmentStore
    {
        public List<AttachmentDownload> Downloads { get; } = [];
        public List<AttachmentUpload> Uploads { get; } = [];
        public List<(Guid Id, byte[] Data, string ContentType)> Saved { get; } = [];
        public List<Guid> Uploaded { get; } = [];
        public List<(Guid Id, string Error, DateTimeOffset? RetryAt)> Failed { get; } = [];
        public List<(Guid Id, string Reason)> Skipped { get; } = [];

        public Task<IReadOnlyList<AttachmentDownload>> GetDownloadsDueAsync(int take, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AttachmentDownload>>(Downloads.ToList());

        public Task SaveDownloadedAsync(Guid id, byte[] data, string contentType, CancellationToken cancellationToken)
        {
            Saved.Add((id, data, contentType));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AttachmentUpload>> GetUploadsDueAsync(int take, DateTimeOffset now, Guid? defaultProjectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AttachmentUpload>>(Uploads.ToList());

        public Task MarkUploadedAsync(Guid id, Guid planeAttachmentId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Uploaded.Add(id);
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(Guid id, string error, DateTimeOffset? retryAt, CancellationToken cancellationToken)
        {
            Failed.Add((id, error, retryAt));
            return Task.CompletedTask;
        }

        public Task MarkSkippedAsync(Guid id, string reason, CancellationToken cancellationToken)
        {
            Skipped.Add((id, reason));
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlane : IPlaneGateway
    {
        public Exception? Failure { get; set; }
        public List<(Guid ProjectId, Guid WorkItemId, string FileName, string ContentType)> Uploaded { get; } = [];
        public bool IsConfigured => true;

        public Task<Guid> UploadAttachmentAsync(Guid projectId, Guid workItemId, string fileName, string contentType, byte[] data, CancellationToken cancellationToken)
        {
            if (Failure is not null)
                return Task.FromException<Guid>(Failure);
            Uploaded.Add((projectId, workItemId, fileName, contentType));
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<IReadOnlyList<PlaneProject>> ListProjectsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PlaneIntakeCreated> CreateIntakeAsync(Guid projectId, string title, string descriptionHtml, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddCommentAsync(Guid projectId, Guid workItemId, string commentHtml, string externalId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PlaneItemStatus> GetIntakeStatusAsync(Guid projectId, Guid workItemId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
