using Intellifer.Hub.Application;
using Intellifer.Hub.Application.Options;
using Intellifer.Hub.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Intellifer.Hub.Application.Tests;

public sealed class AppErrorIntakeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 7, 45, 0, TimeSpan.Zero);
    private readonly FakeErrorStore _store = new();

    [Fact]
    public async Task New_error_is_stored_with_its_project()
    {
        var accepted = await Intake().AcceptAsync(Report("Support Test", "boom"), CancellationToken.None);

        Assert.False(accepted.Duplicate);
        var stored = Assert.Single(_store.Errors);
        Assert.Equal("Support Test", stored.Project);
        Assert.Equal("supporttest", stored.ProjectKey);
    }

    [Fact]
    public async Task Duplicate_while_the_ticket_is_collected_is_counted_not_stored()
    {
        await Intake().AcceptAsync(Report("Support Test", "Timeout after 3000 ms"), CancellationToken.None);
        var second = await Intake().AcceptAsync(Report("support-test", "Timeout after 5000 ms"), CancellationToken.None);

        Assert.True(second.Duplicate);
        Assert.Equal(2, second.Occurrences);
        Assert.Equal(2, Assert.Single(_store.Errors).Occurrences);
    }

    [Fact]
    public async Task Same_message_from_another_place_is_a_different_error()
    {
        await Intake().AcceptAsync(Report("Support Test", "boom", "OrdersController.Save"), CancellationToken.None);
        await Intake().AcceptAsync(Report("Support Test", "boom", "OrdersController.Load"), CancellationToken.None);

        Assert.Equal(2, _store.Errors.Count);
    }

    [Fact]
    public async Task Same_error_of_another_project_is_stored_separately()
    {
        await Intake().AcceptAsync(Report("Support Test", "boom"), CancellationToken.None);
        await Intake().AcceptAsync(Report("KSB", "boom"), CancellationToken.None);

        Assert.Equal(2, _store.Errors.Count);
    }

    [Fact]
    public async Task Repeat_soon_after_its_ticket_is_counted_not_ticketed_again()
    {
        await Intake().AcceptAsync(Report("Support Test", "boom"), CancellationToken.None);
        _store.Errors[0].State = PlaneSyncState.Sent;

        var repeat = await Intake().AcceptAsync(Report("Support Test", "boom"), CancellationToken.None);

        Assert.True(repeat.Duplicate);
        Assert.Single(_store.Errors);
        Assert.Equal(2, _store.Errors[0].Occurrences);
    }

    [Fact]
    public void Fingerprint_ignores_numbers_and_spacing()
    {
        Assert.Equal(
            AppErrorIntake.Fingerprint("Jobs.Run", "Timeout after 3000 ms on port 5432"),
            AppErrorIntake.Fingerprint("Jobs.Run", "Timeout  after 30 ms on port 80"));
    }

    private AppErrorIntake Intake() =>
        new(_store, Microsoft.Extensions.Options.Options.Create(new HubOptions()), new FixedClock(Now));

    private static AppErrorReport Report(string project, string message, string? source = null) =>
        new(project, source, message, Now);
}

public sealed class AppErrorDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 7, 50, 0, TimeSpan.Zero);
    private static readonly PlaneProject SupportTest = new(Guid.NewGuid(), "Support Test", "SUPPORTTEST", true);
    private static readonly PlaneProject Ksb = new(Guid.NewGuid(), "KSB", "KSB", true);
    private static readonly PlaneProject Closed = new(Guid.NewGuid(), "Crane", "CRANE", false);
    private readonly FakeErrorStore _store = new();
    private readonly FakePlane _plane = new([SupportTest, Ksb, Closed]);

    [Fact]
    public async Task Waits_a_minute_after_the_first_report_of_a_project()
    {
        _store.Add("Support Test", "boom", received: Now.AddSeconds(-30));

        Assert.Equal(0, await Dispatcher().RunOnceAsync(CancellationToken.None));
        Assert.Empty(_plane.Created);
    }

    [Fact]
    public async Task Single_error_becomes_a_ticket_titled_with_project_place_and_date()
    {
        _store.Add("Support Test", "Object reference not set\nat IFRMTC.Controllers.DataParserController", "DataParserController.ExtractTables");

        Assert.Equal(1, await Dispatcher().RunOnceAsync(CancellationToken.None));

        var (projectId, title, html) = Assert.Single(_plane.Created);
        Assert.Equal(SupportTest.Id, projectId);
        Assert.Equal("[Error] Support Test / DataParserController.ExtractTables – 2026-11-23 13:15 (UTC+05:30)", title);
        Assert.Contains("<pre><code>Object reference not set\nat IFRMTC.Controllers.DataParserController</code></pre>", html);
    }

    [Fact]
    public async Task Different_errors_of_one_project_share_one_ticket_with_their_counts()
    {
        _store.Add("Support Test", "boom", "A.One", occurrences: 3);
        _store.Add("support test", "bang", "B.Two", received: Now.AddSeconds(-61));

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        var (_, title, html) = Assert.Single(_plane.Created);
        Assert.Equal("[Error] Support Test – 2 different errors – 2026-11-23 13:15 (UTC+05:30)", title);
        Assert.Contains("<h3>1. A.One</h3>", html);
        Assert.Contains("<h3>2. B.Two</h3>", html);
        Assert.Contains("Reported 3 times", html);
        Assert.Contains("2 different errors, 4 reports", html);
        Assert.Equal(2, Assert.Single(_store.SentBatches).Count);
    }

    [Fact]
    public async Task Different_projects_get_different_tickets()
    {
        _store.Add("Support Test", "boom");
        _store.Add("KSB", "boom");

        Assert.Equal(2, await Dispatcher().RunOnceAsync(CancellationToken.None));

        Assert.Equal([SupportTest.Id, Ksb.Id], _plane.Created.Select(item => item.ProjectId).OrderBy(id => id == Ksb.Id));
    }

    [Fact]
    public async Task Unknown_project_is_skipped_with_the_reason()
    {
        _store.Add("No Such Project", "boom");

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Empty(_plane.Created);
        Assert.Contains("No Plane project matches", Assert.Single(_store.Skipped).Reason);
    }

    [Fact]
    public async Task Project_with_intake_off_is_skipped()
    {
        _store.Add("Crane", "boom");

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Contains("Intake is off", Assert.Single(_store.Skipped).Reason);
    }

    [Fact]
    public async Task Error_storm_is_stored_without_a_ticket()
    {
        _store.Add("Support Test", "boom");
        _store.TicketsLastHour = 30;

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Empty(_plane.Created);
        Assert.Contains("limit 30", Assert.Single(_store.Skipped).Reason);
    }

    [Fact]
    public async Task Plane_down_retries_the_whole_batch_later()
    {
        _store.Add("Support Test", "boom");
        _store.Add("Support Test", "bang");
        _plane.Failure = new HttpRequestException("Plane unreachable");

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        var failed = Assert.Single(_store.Failed);
        Assert.Equal(2, failed.Ids.Count);
        Assert.Equal(Now.AddSeconds(30), failed.RetryAt);
    }

    [Fact]
    public async Task Team_of_the_project_is_told_about_the_ticket()
    {
        _store.Add("Support Test", "boom", "DataParserController.ExtractTables");

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        var notice = Assert.Single(_store.TeamNotifications);
        Assert.Null(notice.ConversationId);
        Assert.Equal("Support Test", notice.ClientProject);
        Assert.Equal("SUPPORTTEST-3", notice.TicketNumber);
        Assert.Equal("app errors of Support Test", notice.Customer);
        Assert.Equal("", notice.CustomerKey);
    }

    [Fact]
    public async Task No_team_notification_when_turned_off_or_not_connected()
    {
        _store.Add("Support Test", "boom");
        await Dispatcher(notifyTeam: false).RunOnceAsync(CancellationToken.None);
        _store.Add("KSB", "boom");
        await Dispatcher(teamConnected: false).RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, _store.SentBatches.Count);
        Assert.Empty(_store.TeamNotifications);
    }

    private AppErrorDispatcher Dispatcher(bool notifyTeam = true, bool teamConnected = true)
    {
        var plane = Microsoft.Extensions.Options.Options.Create(new PlaneOptions { NotifyTeam = notifyTeam });
        var clock = new FixedClock(Now);
        return new AppErrorDispatcher(
            _store,
            _plane,
            new PlaneProjectResolver(_plane, plane, clock),
            new FakeTeam(teamConnected),
            Microsoft.Extensions.Options.Options.Create(new HubOptions { TimeZone = "Asia/Kolkata" }),
            plane,
            clock,
            NullLogger<AppErrorDispatcher>.Instance);
    }

    private sealed class FakeTeam(bool connected) : IProjectTeamDirectory
    {
        public bool IsConfigured => connected;

        public Task<IReadOnlyList<TeamMember>> GetTeamAsync(IReadOnlyList<string> projectNames, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TeamMember>>([]);
    }

    private sealed class FakePlane(IReadOnlyList<PlaneProject> projects) : IPlaneGateway
    {
        public Exception? Failure { get; set; }
        public List<(Guid ProjectId, string Title, string Html)> Created { get; } = [];
        public bool IsConfigured => true;

        public Task<IReadOnlyList<PlaneProject>> ListProjectsAsync(CancellationToken cancellationToken) => Task.FromResult(projects);

        public Task<PlaneIntakeCreated> CreateIntakeAsync(Guid projectId, string title, string descriptionHtml, CancellationToken cancellationToken)
        {
            if (Failure is not null)
                return Task.FromException<PlaneIntakeCreated>(Failure);
            Created.Add((projectId, title, descriptionHtml));
            return Task.FromResult(new PlaneIntakeCreated(Guid.NewGuid(), Guid.NewGuid(), 3));
        }

        public Task AddCommentAsync(Guid projectId, Guid workItemId, string commentHtml, string externalId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PlaneItemStatus> GetIntakeStatusAsync(Guid projectId, Guid workItemId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Guid> UploadAttachmentAsync(Guid projectId, Guid workItemId, string fileName, string contentType, byte[] data, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>In-memory app error store with the same matching rules as the real one.</summary>
internal sealed class FakeErrorStore : IAppErrorStore
{
    internal sealed class Row
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Project { get; init; } = "";
        public string ProjectKey { get; init; } = "";
        public string? Source { get; init; }
        public string Message { get; init; } = "";
        public string Fingerprint { get; init; } = "";
        public DateTimeOffset OccurredAt { get; init; }
        public DateTimeOffset ReceivedAt { get; init; }
        public int Occurrences { get; set; } = 1;
        public DateTimeOffset LastSeenAt { get; set; }
        public PlaneSyncState State { get; set; } = PlaneSyncState.Pending;
    }

    private static readonly DateTimeOffset DefaultReceived = new(2026, 11, 23, 7, 45, 0, TimeSpan.Zero);

    public List<Row> Errors { get; } = [];
    public int TicketsLastHour { get; set; }
    public List<IReadOnlyList<Guid>> SentBatches { get; } = [];
    public List<TeamNotificationDraft> TeamNotifications { get; } = [];
    public List<(IReadOnlyList<Guid> Ids, string Reason)> Skipped { get; } = [];
    public List<(IReadOnlyList<Guid> Ids, DateTimeOffset? RetryAt)> Failed { get; } = [];

    /// <summary>A pending error, received (by default) well before the batch window closed.</summary>
    public void Add(string project, string message, string? source = null, int occurrences = 1, DateTimeOffset? received = null) =>
        Errors.Add(new Row
        {
            Project = project,
            ProjectKey = AppErrorIntake.ProjectKey(project),
            Source = source,
            Message = message,
            Fingerprint = AppErrorIntake.Fingerprint(source, message),
            OccurredAt = DefaultReceived,
            ReceivedAt = received ?? DefaultReceived,
            LastSeenAt = received ?? DefaultReceived,
            Occurrences = occurrences
        });

    public Task<Guid?> FindPendingAsync(string projectKey, string fingerprint, CancellationToken cancellationToken) =>
        Task.FromResult(Errors.Where(row => row.ProjectKey == projectKey && row.Fingerprint == fingerprint && row.State == PlaneSyncState.Pending)
            .Select(row => (Guid?)row.Id).LastOrDefault());

    public Task<Guid?> FindTicketedAsync(string projectKey, string fingerprint, DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult(Errors.Where(row => row.ProjectKey == projectKey && row.Fingerprint == fingerprint && row.State == PlaneSyncState.Sent && row.LastSeenAt >= since)
            .Select(row => (Guid?)row.Id).LastOrDefault());

    public Task<int> CountRepeatAsync(Guid errorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = Errors.Single(item => item.Id == errorId);
        row.Occurrences++;
        row.LastSeenAt = now;
        return Task.FromResult(row.Occurrences);
    }

    public Task<Guid> AddAsync(AppErrorReport report, string projectKey, string fingerprint, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = new Row
        {
            Project = report.Project,
            ProjectKey = projectKey,
            Source = report.Source,
            Message = report.Message,
            Fingerprint = fingerprint,
            OccurredAt = report.OccurredAt ?? now,
            ReceivedAt = now,
            LastSeenAt = now
        };
        Errors.Add(row);
        return Task.FromResult(row.Id);
    }

    public Task<IReadOnlyList<AppErrorJob>> GetPendingAsync(int take, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AppErrorJob>>(Errors
            .Where(row => row.State == PlaneSyncState.Pending)
            .Select(row => new AppErrorJob(row.Id, row.Project, row.ProjectKey, row.Source, row.Message, row.OccurredAt, row.ReceivedAt, row.Occurrences, 0))
            .ToList());

    public Task<int> CountTicketsSinceAsync(string projectKey, DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult(TicketsLastHour);

    public Task MarkSentAsync(IReadOnlyList<Guid> ids, Guid projectId, PlaneIntakeCreated created, TeamNotificationDraft? teamNotification, DateTimeOffset now, CancellationToken cancellationToken)
    {
        SentBatches.Add(ids);
        Set(ids, PlaneSyncState.Sent);
        if (teamNotification is not null)
            TeamNotifications.Add(teamNotification);
        return Task.CompletedTask;
    }

    public Task MarkSkippedAsync(IReadOnlyList<Guid> ids, string reason, CancellationToken cancellationToken)
    {
        Skipped.Add((ids, reason));
        Set(ids, PlaneSyncState.Skipped);
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(IReadOnlyList<Guid> ids, string error, DateTimeOffset? retryAt, CancellationToken cancellationToken)
    {
        Failed.Add((ids, retryAt));
        return Task.CompletedTask;
    }

    private void Set(IReadOnlyList<Guid> ids, PlaneSyncState state)
    {
        foreach (var row in Errors.Where(row => ids.Contains(row.Id)))
            row.State = state;
    }
}
