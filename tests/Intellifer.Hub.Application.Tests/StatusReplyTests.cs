using Intellifer.Hub.Application;
using Intellifer.Hub.Application.Options;
using Intellifer.Hub.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Intellifer.Hub.Application.Tests;

public sealed class PlaneStatusNoticeTests
{
    private static PlaneItemStatus Accepted(string state, string group) => new(PlaneItemStatus.Accepted, state, group);

    [Fact]
    public void Accepting_into_backlog_is_one_message()
    {
        var text = PlaneStatusNotice.Describe("-2:triage", Accepted("Backlog", "backlog"));
        Assert.Equal("has been accepted by our team and added to our backlog (Backlog).", text);
    }

    [Theory]
    [InlineData("Todo", "unstarted", "is planned and on our to-do list (Todo).")]
    [InlineData("In Progress", "started", "is now in progress (In Progress).")]
    [InlineData("Done", "completed", "has been completed (Done).")]
    [InlineData("Cancelled", "cancelled", "has been cancelled (Cancelled).")]
    public void Each_stage_after_acceptance_has_its_message(string state, string group, string expected)
    {
        Assert.Equal(expected, PlaneStatusNotice.Describe("1:backlog", Accepted(state, group)));
    }

    [Fact]
    public void Declined_and_duplicate_are_explained()
    {
        Assert.Contains("will not be taken forward", PlaneStatusNotice.Describe("-2:triage", new(PlaneItemStatus.Declined, "Triage", "triage")));
        Assert.Contains("existing request", PlaneStatusNotice.Describe("-2:triage", new(PlaneItemStatus.Duplicate, "Triage", "triage")));
    }

    [Theory]
    [InlineData("1:started", PlaneItemStatus.Accepted, "started")]   // unchanged
    [InlineData("-2:triage", PlaneItemStatus.Snoozed, "triage")]    // snoozed is internal
    [InlineData("1:backlog", PlaneItemStatus.Accepted, "backlog")]  // another backlog state
    public void No_message_when_nothing_the_customer_cares_about_changed(string previous, int intake, string group)
    {
        Assert.Null(PlaneStatusNotice.Describe(previous, new PlaneItemStatus(intake, "x", group)));
    }

    [Theory]
    [InlineData(PlaneItemStatus.Accepted, "completed", true)]
    [InlineData(PlaneItemStatus.Accepted, "cancelled", true)]
    [InlineData(PlaneItemStatus.Declined, "triage", true)]
    [InlineData(PlaneItemStatus.Duplicate, "triage", true)]
    [InlineData(PlaneItemStatus.Accepted, "started", false)]
    [InlineData(PlaneItemStatus.Pending, "triage", false)]
    public void Closed_items_stop_being_polled(int intake, string group, bool closed)
    {
        Assert.Equal(closed, new PlaneItemStatus(intake, null, group).IsClosed);
    }
}

public sealed class PlaneStatusWatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly PlaneProject Project = new(Guid.NewGuid(), "planeinf", "PLANE", true);
    private readonly FakeStore _store = new();
    private readonly FakePlane _plane = new();

    [Fact]
    public async Task First_check_only_records_the_status()
    {
        _store.Items.Add(Item(ChannelType.Email, statusKey: null));
        _plane.Status = new PlaneItemStatus(PlaneItemStatus.Accepted, "Done", "completed");

        Assert.Equal(0, await Watcher().RunOnceAsync(CancellationToken.None));

        var saved = Assert.Single(_store.Saved);
        Assert.Equal(("1:completed", true), (saved.Key, saved.Closed));
        Assert.Null(saved.Reply);
    }

    [Fact]
    public async Task Email_change_is_a_threaded_reply_to_the_customer()
    {
        _store.Items.Add(Item(ChannelType.Email, statusKey: "-2:triage"));
        _plane.Status = new PlaneItemStatus(PlaneItemStatus.Accepted, "In Progress", "started");

        Assert.Equal(1, await Watcher().RunOnceAsync(CancellationToken.None));

        var reply = Assert.Single(_store.Saved).Reply!;
        Assert.Equal(ChannelType.Email, reply.Channel);
        Assert.Equal("asha@example.com", reply.Recipient);
        Assert.Equal("thread-1", reply.ThreadKey);
        Assert.Equal("Re: Login error", reply.Subject);
        Assert.Contains("Hello Asha,", reply.Body);
        Assert.Contains("request PLANE-12: it has been accepted by our team and is now in progress (In Progress).", reply.Body);
        Assert.Contains("Intellifer Support", reply.Body);
    }

    [Fact]
    public async Task Whatsapp_change_goes_to_the_chat()
    {
        _store.Items.Add(Item(ChannelType.WhatsApp, statusKey: "1:started", thread: "107589064999080@lid"));
        _plane.Status = new PlaneItemStatus(PlaneItemStatus.Accepted, "Done", "completed");

        await Watcher().RunOnceAsync(CancellationToken.None);

        var saved = Assert.Single(_store.Saved);
        Assert.True(saved.Closed);
        Assert.Equal("107589064999080@lid", saved.Reply!.Recipient);
        Assert.Null(saved.Reply.Subject);
        Assert.Contains("PLANE-12: it has been completed (Done).", saved.Reply.Body);
    }

    [Fact]
    public async Task Turned_off_does_nothing()
    {
        _store.Items.Add(Item(ChannelType.Email, statusKey: "-2:triage"));

        await Watcher(statusReplies: false).RunOnceAsync(CancellationToken.None);

        Assert.Empty(_store.Saved);
        Assert.Equal(0, _plane.Reads);
    }

    [Fact]
    public async Task Plane_error_leaves_the_item_for_the_next_run()
    {
        _store.Items.Add(Item(ChannelType.Email, statusKey: "-2:triage"));
        _plane.Failure = new HttpRequestException("timeout");

        await Watcher().RunOnceAsync(CancellationToken.None);

        Assert.Empty(_store.Saved);
    }

    private PlaneStatusWatcher Watcher(bool statusReplies = true)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PlaneOptions { StatusReplies = statusReplies });
        var clock = new FixedClock();
        return new PlaneStatusWatcher(
            _store,
            _plane,
            new PlaneProjectResolver(_plane, options, clock),
            options,
            clock,
            NullLogger<PlaneStatusWatcher>.Instance);
    }

    private static TrackedIntake Item(ChannelType channel, string? statusKey, string thread = "thread-1") => new(
        Guid.NewGuid(),
        channel,
        thread,
        channel == ChannelType.Email ? "asha@example.com" : "+919876543210",
        "Asha",
        channel == ChannelType.Email ? "Login error" : null,
        Project.Id,
        Guid.NewGuid(),
        12,
        statusKey);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeStore : IPlaneStatusStore
    {
        public List<TrackedIntake> Items { get; } = [];
        public List<(string Key, bool Closed, OutboundDraft? Reply)> Saved { get; } = [];

        public Task<IReadOnlyList<TrackedIntake>> GetDueAsync(int take, DateTimeOffset checkedBefore, Guid? defaultProjectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TrackedIntake>>(Items.Take(take).ToList());

        public Task SaveAsync(Guid conversationId, string statusKey, bool closed, DateTimeOffset now, OutboundDraft? reply, CancellationToken cancellationToken)
        {
            Saved.Add((statusKey, closed, reply));
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlane : IPlaneGateway
    {
        public PlaneItemStatus Status { get; set; } = new(PlaneItemStatus.Pending, "Triage", "triage");
        public Exception? Failure { get; set; }
        public int Reads { get; private set; }
        public bool IsConfigured => true;

        public Task<IReadOnlyList<PlaneProject>> ListProjectsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PlaneProject>>([Project]);

        public Task<Guid> UploadAttachmentAsync(Guid projectId, Guid workItemId, string fileName, string contentType, byte[] data, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PlaneItemStatus> GetIntakeStatusAsync(Guid projectId, Guid workItemId, CancellationToken cancellationToken)
        {
            Reads++;
            return Failure is null ? Task.FromResult(Status) : Task.FromException<PlaneItemStatus>(Failure);
        }

        public Task<PlaneIntakeCreated> CreateIntakeAsync(Guid projectId, string title, string descriptionHtml, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddCommentAsync(Guid projectId, Guid workItemId, string commentHtml, string externalId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

public sealed class OutboundDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeQueue _queue = new();

    [Fact]
    public async Task Each_reply_goes_out_on_its_own_channel()
    {
        var email = new FakeSender(ChannelType.Email);
        var whatsapp = new FakeSender(ChannelType.WhatsApp);
        _queue.Due.AddRange([Job(ChannelType.Email), Job(ChannelType.WhatsApp)]);

        Assert.Equal(2, await Dispatcher(email, whatsapp).RunOnceAsync(CancellationToken.None));

        Assert.Single(email.Sent);
        Assert.Single(whatsapp.Sent);
        Assert.Equal(2, _queue.Sent.Count);
    }

    [Fact]
    public async Task Whatsapp_down_retries_later()
    {
        var whatsapp = new FakeSender(ChannelType.WhatsApp) { Failure = new InvalidOperationException("OpenWA returned 503") };
        _queue.Due.Add(Job(ChannelType.WhatsApp));

        await Dispatcher(whatsapp).RunOnceAsync(CancellationToken.None);

        Assert.Equal(Now.AddMinutes(1), Assert.Single(_queue.Failed).RetryAt);
    }

    [Fact]
    public async Task Gives_up_after_ten_attempts()
    {
        var email = new FakeSender(ChannelType.Email) { Failure = new InvalidOperationException("quota") };
        _queue.Due.Add(Job(ChannelType.Email) with { Attempts = 9 });

        await Dispatcher(email).RunOnceAsync(CancellationToken.None);

        Assert.Null(Assert.Single(_queue.Failed).RetryAt);
    }

    private OutboundDispatcher Dispatcher(params IReplySender[] senders) =>
        new(_queue, senders, new FixedClock(), NullLogger<OutboundDispatcher>.Instance);

    private static OutboundJob Job(ChannelType channel) =>
        new(Guid.NewGuid(), Guid.NewGuid(), channel, "someone", "thread", null, "update", 0);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeSender(ChannelType channel) : IReplySender
    {
        public ChannelType Channel => channel;
        public bool IsConfigured => true;
        public Exception? Failure { get; set; }
        public List<OutboundJob> Sent { get; } = [];

        public Task<string?> SendAsync(OutboundJob reply, CancellationToken cancellationToken)
        {
            if (Failure is not null)
                return Task.FromException<string?>(Failure);
            Sent.Add(reply);
            return Task.FromResult<string?>("id-1");
        }
    }

    private sealed class FakeQueue : IOutboundQueue
    {
        public List<OutboundJob> Due { get; } = [];
        public List<Guid> Sent { get; } = [];
        public List<(Guid Id, DateTimeOffset? RetryAt)> Failed { get; } = [];

        public Task<IReadOnlyList<OutboundJob>> GetDueAsync(int take, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OutboundJob>>(Due.Take(take).ToList());

        public Task MarkSentAsync(Guid id, string? externalId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Sent.Add(id);
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(Guid id, string error, DateTimeOffset? retryAt, CancellationToken cancellationToken)
        {
            Failed.Add((id, retryAt));
            return Task.CompletedTask;
        }
    }
}
