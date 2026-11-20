using Intellifer.Hub.Application;
using Intellifer.Hub.Application.Options;
using Intellifer.Hub.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Intellifer.Hub.Application.Tests;

public sealed class AppErrorIntakeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 13, 3, 0, TimeSpan.Zero);
    private static readonly AppInfo App = new(Guid.NewGuid(), "crm-backend", "Support Test", "hubapp_AbCd…", true, Now, null);
    private readonly FakeErrorStore _store = new();

    [Fact]
    public async Task New_error_is_queued_for_plane()
    {
        var accepted = await Intake().AcceptAsync(App, new AppErrorReport("NullReferenceException in OrderService", "orders", "error", null), CancellationToken.None);

        Assert.False(accepted.Repeat);
        Assert.Equal("queued for Plane", accepted.Status);
        Assert.Null(Assert.Single(_store.Added).SkipReason);
    }

    [Fact]
    public async Task Same_error_within_the_hour_is_counted_not_ticketed_again()
    {
        var first = await Intake().AcceptAsync(App, new AppErrorReport("Timeout after 3001 ms calling 10.0.0.12:5432", null, null, null), CancellationToken.None);
        var again = await Intake().AcceptAsync(App, new AppErrorReport("Timeout after 2998 ms calling 10.0.0.15:5432", null, null, null), CancellationToken.None);

        Assert.Single(_store.Added);
        Assert.True(again.Repeat);
        Assert.Equal(first.ErrorId, again.ErrorId);
        Assert.Equal(2, again.Occurrences);
    }

    [Fact]
    public async Task Error_storm_is_stored_but_not_ticketed()
    {
        _store.TicketedLastHour = 30;

        var accepted = await Intake().AcceptAsync(App, new AppErrorReport("disk full", null, null, null), CancellationToken.None);

        Assert.Contains("not ticketed", accepted.Status);
        Assert.Contains("limit 30", Assert.Single(_store.Added).SkipReason);
    }

    [Fact]
    public void Fingerprint_ignores_numbers_and_ids_but_not_words()
    {
        Assert.Equal(
            AppErrorIntake.Fingerprint("Order 1182 failed: id 0x7ffe3a, at 12:01:33"),
            AppErrorIntake.Fingerprint("order 99 failed:   id 0x1b2c, at 18:44:02"));
        Assert.NotEqual(
            AppErrorIntake.Fingerprint("Order 1182 failed"),
            AppErrorIntake.Fingerprint("Invoice 1182 failed"));
    }

    [Fact]
    public void App_keys_are_random_and_only_their_hash_is_kept()
    {
        var key = AppKeys.NewKey();

        Assert.StartsWith("hubapp_", key);
        Assert.Equal(47, key.Length);
        Assert.NotEqual(key, AppKeys.NewKey());
        Assert.Equal(64, AppKeys.Hash(key).Length);
        Assert.Equal(AppKeys.Hash(key), AppKeys.Hash(" " + key + " "));
        Assert.Equal(key[..11] + "…", AppKeys.Hint(key));
    }

    private AppErrorIntake Intake() => new(
        _store,
        Microsoft.Extensions.Options.Options.Create(new HubOptions()),
        new FixedClock(Now),
        NullLogger<AppErrorIntake>.Instance);
}

public sealed class AppErrorDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 13, 3, 0, TimeSpan.Zero);
    private static readonly PlaneProject SupportTest = new(Guid.NewGuid(), "Support Test", "SUPPORTTEST", true);
    private static readonly PlaneProject Closed = new(Guid.NewGuid(), "Crane", "CRANE", false);
    private readonly FakeErrorStore _store = new();
    private readonly FakePlane _plane = new([SupportTest, Closed]);

    [Fact]
    public async Task Error_becomes_an_intake_titled_error_date_and_source()
    {
        _store.Due.Add(Job("support test", "Unhandled <exception>\n   at OrderService.Save()", source: "orders"));

        Assert.Equal(1, await Dispatcher().RunOnceAsync(CancellationToken.None));

        var (projectId, title, html) = Assert.Single(_plane.Created);
        Assert.Equal(SupportTest.Id, projectId);
        Assert.Equal("[Error] crm-backend / orders – 2026-11-20 18:33 (UTC+05:30)", title);
        Assert.Contains("<pre><code>Unhandled &lt;exception&gt;\n   at OrderService.Save()</code></pre>", html);
        Assert.Contains("<strong>Project:</strong> Support Test (SUPPORTTEST)", html);
        Assert.Equal(SupportTest.Id, Assert.Single(_store.Sent).ProjectId);
    }

    [Fact]
    public async Task Project_code_also_matches()
    {
        _store.Due.Add(Job("SUPPORTTEST", "boom"));

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Equal(SupportTest.Id, Assert.Single(_plane.Created).ProjectId);
    }

    [Fact]
    public async Task Unknown_project_is_skipped_with_the_reason()
    {
        _store.Due.Add(Job("No Such Project", "boom"));

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Empty(_plane.Created);
        Assert.Contains("has no matching Plane project", Assert.Single(_store.Skipped).Reason);
    }

    [Fact]
    public async Task Project_with_intake_off_is_skipped()
    {
        _store.Due.Add(Job("Crane", "boom"));

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Contains("Intake is off", Assert.Single(_store.Skipped).Reason);
    }

    [Fact]
    public async Task Plane_down_retries_later()
    {
        _store.Due.Add(Job("Support Test", "boom"));
        _plane.Failure = new HttpRequestException("Plane unreachable");

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Equal(Now.AddSeconds(30), Assert.Single(_store.Failed).RetryAt);
    }

    private AppErrorDispatcher Dispatcher()
    {
        var plane = Microsoft.Extensions.Options.Options.Create(new PlaneOptions());
        var clock = new FixedClock(Now);
        return new AppErrorDispatcher(
            _store,
            _plane,
            new PlaneProjectResolver(_plane, plane, clock),
            Microsoft.Extensions.Options.Options.Create(new HubOptions { TimeZone = "Asia/Kolkata" }),
            clock,
            NullLogger<AppErrorDispatcher>.Instance);
    }

    private static AppErrorJob Job(string project, string message, string? source = null) =>
        new(Guid.NewGuid(), "crm-backend", project, message, source, "error", Now, 1, 0);

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

internal sealed class FakeErrorStore : IAppErrorStore
{
    public List<(Guid Id, string Fingerprint, string? SkipReason)> Added { get; } = [];
    public Dictionary<Guid, int> Occurrences { get; } = [];
    public int TicketedLastHour { get; set; }
    public List<AppErrorJob> Due { get; } = [];
    public List<(Guid Id, Guid ProjectId)> Sent { get; } = [];
    public List<(Guid Id, string Reason)> Skipped { get; } = [];
    public List<(Guid Id, DateTimeOffset? RetryAt)> Failed { get; } = [];

    public Task<Guid?> FindRecentAsync(Guid appId, string fingerprint, DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult(Added.Where(item => item.Fingerprint == fingerprint).Select(item => (Guid?)item.Id).LastOrDefault());

    public Task<int> CountRepeatAsync(Guid errorId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(Occurrences[errorId] += 1);

    public Task<int> CountTicketedSinceAsync(Guid appId, DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult(TicketedLastHour);

    public Task<Guid> AddAsync(Guid appId, AppErrorReport report, string fingerprint, DateTimeOffset now, string? skipReason, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        Added.Add((id, fingerprint, skipReason));
        Occurrences[id] = 1;
        return Task.FromResult(id);
    }

    public Task<IReadOnlyList<AppErrorJob>> GetDueAsync(int take, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AppErrorJob>>(Due.ToList());

    public Task MarkSentAsync(Guid id, Guid projectId, PlaneIntakeCreated created, CancellationToken cancellationToken)
    {
        Sent.Add((id, projectId));
        return Task.CompletedTask;
    }

    public Task MarkSkippedAsync(Guid id, string reason, CancellationToken cancellationToken)
    {
        Skipped.Add((id, reason));
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(Guid id, string error, DateTimeOffset? retryAt, CancellationToken cancellationToken)
    {
        Failed.Add((id, retryAt));
        return Task.CompletedTask;
    }
}
