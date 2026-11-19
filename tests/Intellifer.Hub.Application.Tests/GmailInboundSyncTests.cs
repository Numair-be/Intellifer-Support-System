using Intellifer.Hub.Application;
using Intellifer.Hub.Application.Options;
using Intellifer.Hub.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Intellifer.Hub.Application.Tests;

public sealed class GmailInboundSyncTests
{
    [Fact]
    public async Task Missing_credentials_are_reported_and_do_not_call_gmail()
    {
        var mailbox = new FakeMailbox { IsConfigured = false };
        var health = new MemoryHealth();
        var sync = Create(mailbox, new MemoryHistory(), new MemoryIntake(), health);

        await sync.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, mailbox.ProfileCalls);
        Assert.Equal("not-configured", Assert.Single(health.Rows).Status);
    }

    [Fact]
    public async Task First_poll_stores_customer_mail_and_skips_mail_we_sent()
    {
        var mailbox = new FakeMailbox
        {
            Profile = new GmailProfile("support@example.com", "100"),
            RecentIds = ["customer", "ours"],
            History = new GmailHistoryPage("150", ["later"])
        };
        mailbox.Messages["customer"] = Message("customer", "thread-1", "Ada <ada@example.com>", "Help", "The printer is offline.\nOn Monday, Ada wrote:\nstale");
        mailbox.Messages["ours"] = Message("ours", "thread-2", "Support <support@example.com>", "Re: Help", "We see it");
        mailbox.Messages["later"] = Message("later", "thread-1", "ada@example.com", "Help", "Still offline", ["SENT"]);

        var intake = new MemoryIntake();
        var history = new MemoryHistory();
        var sync = Create(mailbox, history, intake, new MemoryHealth());

        await sync.RunOnceAsync(CancellationToken.None);

        var stored = Assert.Single(intake.Messages);
        Assert.Equal("customer", stored.ExternalMessageId);
        Assert.Equal("ada@example.com", stored.SenderNormalized);
        Assert.Equal("The printer is offline.", stored.Body);
        Assert.Equal(["customer"], mailbox.Labeled);
        Assert.Equal("150", history.HistoryIds["support@example.com"]);
    }

    [Fact]
    public async Task Expired_history_reads_recent_mail_and_resets_the_cursor()
    {
        var mailbox = new FakeMailbox
        {
            Profile = new GmailProfile("support@example.com", "900"),
            HistoryExpired = true,
            RecentIds = ["recent"]
        };
        mailbox.Messages["recent"] = Message("recent", "thread-9", "ada@example.com", "Hi", "Hello");
        var history = new MemoryHistory();
        history.HistoryIds["support@example.com"] = "1";
        var intake = new MemoryIntake();
        var sync = Create(mailbox, history, intake, new MemoryHealth());

        await sync.RunOnceAsync(CancellationToken.None);

        Assert.Equal("recent", Assert.Single(intake.Messages).ExternalMessageId);
        Assert.Equal("900", history.HistoryIds["support@example.com"]);
    }

    [Fact]
    public async Task Screenshots_are_kept_and_signature_logos_and_huge_images_are_not()
    {
        var mailbox = new FakeMailbox { RecentIds = ["customer"] };
        mailbox.AttachmentData["att-1"] = new byte[20_000];
        mailbox.AttachmentData["att-3"] = new byte[974];
        mailbox.Messages["customer"] = Message("customer", "thread-1", "ada@example.com", "Error", "See screenshot") with
        {
            Images =
            [
                new GmailImagePart("screenshot.png", "image/png", 20_000, "att-1", null),
                new GmailImagePart("logo.png", "image/png", 900, null, new byte[900], IsInline: true),
                new GmailImagePart("tiny-but-attached.png", "image/png", 974, "att-3", null),
                new GmailImagePart("huge.jpg", "image/jpeg", 9 * 1024 * 1024, "att-2", null)
            ]
        };
        var intake = new MemoryIntake();

        await Create(mailbox, new MemoryHistory(), intake, new MemoryHealth()).RunOnceAsync(CancellationToken.None);

        var attachments = Assert.Single(intake.Messages).Attachments;
        Assert.Equal(["screenshot.png", "tiny-but-attached.png", "huge.jpg"], attachments.Select(attachment => attachment.FileName));
        Assert.Equal(20_000, attachments[0].Data!.Length);
        Assert.Equal(974, attachments[1].Data!.Length);
        Assert.Null(attachments[2].Data);
        Assert.Contains("limit is 5 MB", attachments[2].SkipReason);
    }

    private static GmailInboundSync Create(
        FakeMailbox mailbox,
        MemoryHistory history,
        MemoryIntake intake,
        MemoryHealth health) =>
        new(
            mailbox,
            history,
            intake,
            new SenderNormalizer(Microsoft.Extensions.Options.Options.Create(new HubOptions())),
            health,
            Microsoft.Extensions.Options.Options.Create(new GmailOptions { BootstrapDays = 7, BootstrapMaxMessages = 20 }),
            Microsoft.Extensions.Options.Options.Create(new PlaneOptions()),
            NullLogger<GmailInboundSync>.Instance);

    private static GmailFetchedMessage Message(
        string id,
        string threadId,
        string from,
        string subject,
        string body,
        IReadOnlyList<string>? labels = null) =>
        new(id, threadId, from, subject, body, false, labels ?? [], [], DateTimeOffset.UnixEpoch);

    private sealed class FakeMailbox : IGmailMailbox
    {
        public bool IsConfigured { get; set; } = true;
        public GmailProfile Profile { get; set; } = new("support@example.com", "100");
        public List<string> RecentIds { get; set; } = [];
        public GmailHistoryPage History { get; set; } = new("100", []);
        public bool HistoryExpired { get; set; }
        public Dictionary<string, GmailFetchedMessage> Messages { get; } = [];
        public List<string> Labeled { get; } = [];
        public int ProfileCalls { get; private set; }

        public Task<GmailProfile> GetProfileAsync(CancellationToken cancellationToken)
        {
            ProfileCalls++;
            return Task.FromResult(Profile);
        }

        public Task<GmailHistoryPage> ListHistoryAsync(string startHistoryId, CancellationToken cancellationToken)
        {
            if (HistoryExpired)
                throw new GmailHistoryExpiredException();
            return Task.FromResult(History);
        }

        public Task<IReadOnlyList<string>> ListRecentMessageIdsAsync(int newerThanDays, int maxMessages, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(RecentIds);

        public Task<GmailFetchedMessage?> GetMessageAsync(string messageId, CancellationToken cancellationToken) =>
            Task.FromResult(Messages.TryGetValue(messageId, out var message) ? message : null);

        public Dictionary<string, byte[]> AttachmentData { get; } = [];

        public Task<byte[]> GetAttachmentAsync(string messageId, string attachmentId, CancellationToken cancellationToken) =>
            Task.FromResult(AttachmentData[attachmentId]);

        public Task ApplyProcessedLabelAsync(string messageId, CancellationToken cancellationToken)
        {
            Labeled.Add(messageId);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryHistory : IGmailSyncStateStore
    {
        public Dictionary<string, string> HistoryIds { get; } = [];

        public Task<string?> GetHistoryIdAsync(string mailbox, CancellationToken cancellationToken) =>
            Task.FromResult(HistoryIds.TryGetValue(mailbox, out var historyId) ? historyId : null);

        public Task SetHistoryIdAsync(string mailbox, string historyId, CancellationToken cancellationToken)
        {
            HistoryIds[mailbox] = historyId;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryIntake : IInboundIntake
    {
        public List<InboundMessage> Messages { get; } = [];

        public Task<IntakeResult> AcceptAsync(InboundMessage message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.FromResult(IntakeResult.Stored);
        }
    }

    private sealed class MemoryHealth : IChannelHealthStore
    {
        public List<ChannelHealthSnapshot> Rows { get; } = [];

        public Task UpsertAsync(ChannelType channel, string status, string? detail, CancellationToken cancellationToken)
        {
            Rows.RemoveAll(row => row.Channel == channel);
            Rows.Add(new ChannelHealthSnapshot(channel, status, detail, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task<ChannelHealthSnapshot?> GetAsync(ChannelType channel, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.SingleOrDefault(row => row.Channel == channel));

        public Task<IReadOnlyList<ChannelHealthSnapshot>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ChannelHealthSnapshot>>(Rows);
    }
}
