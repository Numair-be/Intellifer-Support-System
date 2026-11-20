using Intellifer.Hub.Application;
using Intellifer.Hub.Application.Options;
using Intellifer.Hub.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Intellifer.Hub.Application.Tests;

public sealed class TeamNotifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProjectId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly FakeStore _store = new();
    private readonly FakeTeam _team = new();

    [Fact]
    public async Task Each_member_gets_a_whatsapp_message_with_the_ticket_and_a_link()
    {
        _team.Members = [new TeamMember("Ravi", "ravi@intellifer.in", "+91 98765 43210")];
        _store.Due.Add(Job());

        Assert.Equal(1, await Notifier().RunOnceAsync(CancellationToken.None));

        var (_, messages, note) = Assert.Single(_store.Completed);
        Assert.Null(note);
        var message = Assert.Single(messages);
        Assert.Equal((ChannelType.WhatsApp, "919876543210@c.us"), (message.Channel, message.Recipient));
        Assert.Contains("New intake for project Support Test: SUPPORTTEST-3 – Upload page shows an error", message.Body);
        Assert.Contains("From: Numair (Intellifer)", message.Body);
        Assert.Contains("Kindly update it as soon as possible.", message.Body);
        Assert.Contains($"https://plane.example.com/planeinf/projects/{ProjectId}/intake", message.Body);
        Assert.Equal(["Support Test", "Support Test"], _team.LastNames);
    }

    [Fact]
    public async Task Member_without_a_phone_gets_a_new_email()
    {
        _team.Members = [new TeamMember("Asha", "asha@intellifer.in", null)];
        _store.Due.Add(Job());

        await Notifier().RunOnceAsync(CancellationToken.None);

        var message = Assert.Single(Assert.Single(_store.Completed).Messages);
        Assert.Equal((ChannelType.Email, "asha@intellifer.in", ""), (message.Channel, message.Recipient, message.ThreadKey));
        Assert.Equal("New intake SUPPORTTEST-3 for Support Test: Upload page shows an error", message.Subject);
        Assert.StartsWith("Hello Asha,", message.Body);
    }

    [Fact]
    public async Task Both_channels_when_set()
    {
        _team.Members = [new TeamMember("Ravi", "ravi@intellifer.in", "9876543210")];
        _store.Due.Add(Job());

        await Notifier(channels: "both").RunOnceAsync(CancellationToken.None);

        Assert.Equal(
            [ChannelType.WhatsApp, ChannelType.Email],
            Assert.Single(_store.Completed).Messages.Select(message => message.Channel));
    }

    [Fact]
    public async Task The_customer_is_not_told_about_their_own_report()
    {
        _team.Members =
        [
            new TeamMember("Numair", "numair@intellifer.in", "+91 98765 43210"),
            new TeamMember("Ravi", null, "+91 91234 56789")
        ];
        _store.Due.Add(Job() with { CustomerKey = "+919876543210" });

        await Notifier().RunOnceAsync(CancellationToken.None);

        Assert.Equal("919123456789@c.us", Assert.Single(Assert.Single(_store.Completed).Messages).Recipient);
    }

    [Fact]
    public async Task No_team_members_is_recorded_not_retried()
    {
        _team.Members = [new TeamMember("Nobody", null, null)];
        _store.Due.Add(Job());

        await Notifier().RunOnceAsync(CancellationToken.None);

        var (_, messages, note) = Assert.Single(_store.Completed);
        Assert.Empty(messages);
        Assert.Contains("No team members", note);
    }

    [Fact]
    public async Task Lookup_failure_retries_later()
    {
        _team.Failure = new HttpRequestException("Odoo unreachable");
        _store.Due.Add(Job());

        await Notifier().RunOnceAsync(CancellationToken.None);

        Assert.Empty(_store.Completed);
        Assert.Equal(Now.AddSeconds(30), Assert.Single(_store.Failed).RetryAt);
    }

    [Fact]
    public async Task Does_nothing_until_the_team_lookup_is_connected()
    {
        _store.Due.Add(Job());

        Assert.Equal(0, await new TeamNotifier(
            _store,
            new UnconfiguredProjectTeamDirectory(),
            Normalizer(),
            Microsoft.Extensions.Options.Options.Create(new PlaneOptions()),
            new FixedClock(),
            NullLogger<TeamNotifier>.Instance).RunOnceAsync(CancellationToken.None));
        Assert.Empty(_store.Completed);
    }

    private TeamNotifier Notifier(string channels = "whatsapp") => new(
        _store,
        _team,
        Normalizer(),
        Microsoft.Extensions.Options.Options.Create(new PlaneOptions
        {
            Url = "https://plane.example.com",
            WorkspaceSlug = "planeinf",
            TeamChannels = channels
        }),
        new FixedClock(),
        NullLogger<TeamNotifier>.Instance);

    private static SenderNormalizer Normalizer() =>
        new(Microsoft.Extensions.Options.Options.Create(new HubOptions { DefaultPhoneRegion = "IN" }));

    private static TeamNotificationJob Job() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "Support Test",
        null,
        "Support Test",
        ProjectId,
        "SUPPORTTEST-3",
        "Upload page shows an error",
        "Numair (Intellifer)",
        "customer@example.com",
        0);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeTeam : IProjectTeamDirectory
    {
        public IReadOnlyList<TeamMember> Members { get; set; } = [];
        public Exception? Failure { get; set; }
        public IReadOnlyList<string> LastNames { get; private set; } = [];
        public bool IsConfigured => true;

        public Task<IReadOnlyList<TeamMember>> GetTeamAsync(IReadOnlyList<string> projectNames, CancellationToken cancellationToken)
        {
            LastNames = projectNames;
            return Failure is null ? Task.FromResult(Members) : Task.FromException<IReadOnlyList<TeamMember>>(Failure);
        }
    }

    private sealed class FakeStore : ITeamNotificationStore
    {
        public List<TeamNotificationJob> Due { get; } = [];
        public List<(Guid Id, IReadOnlyList<OutboundDraft> Messages, string? Note)> Completed { get; } = [];
        public List<(Guid Id, DateTimeOffset? RetryAt)> Failed { get; } = [];

        public Task<IReadOnlyList<TeamNotificationJob>> GetDueAsync(int take, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TeamNotificationJob>>(Due.ToList());

        public Task CompleteAsync(Guid id, IReadOnlyList<OutboundDraft> messages, string? note, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Completed.Add((id, messages, note));
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(Guid id, string error, DateTimeOffset? retryAt, CancellationToken cancellationToken)
        {
            Failed.Add((id, retryAt));
            return Task.CompletedTask;
        }
    }
}
