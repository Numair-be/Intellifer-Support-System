using Intellifer.Hub.Application;
using Intellifer.Hub.Application.Options;
using Intellifer.Hub.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Intellifer.Hub.Application.Tests;

public sealed class PlaneIntakeDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly PlaneProject PlaneInf = new(Guid.NewGuid(), "planeinf", "PLANE", true);
    private static readonly PlaneProject LtValves = new(Guid.NewGuid(), "LTValves", "LNT", true);
    private static readonly PlaneProject LtForm = new(Guid.NewGuid(), "LT FORM IV D", "LTFORMIVD", true);
    private static readonly PlaneProject Closed = new(Guid.NewGuid(), "Crane", "CRANE", false);

    private readonly FakeQueue _queue = new();
    private readonly FakePlane _plane = new([PlaneInf, LtValves, LtForm, Closed]);
    private readonly FakeDirectory _customers = new() { Result = Contact("planeinf") };

    [Fact]
    public async Task Intake_goes_to_the_plane_project_named_like_the_odoo_project()
    {
        _queue.Due.Add(Job(Guid.NewGuid(), "login error", Now));

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        var created = Assert.Single(_plane.Created);
        Assert.Equal(PlaneInf.Id, created.ProjectId);
        Assert.StartsWith("[WhatsApp] Numair: login error", created.Title);
        Assert.Contains("Numair, Intellifer (+919876543210)", created.Html);
        Assert.Equal(PlaneInf.Id, Assert.Single(_queue.IntakeCreated).ProjectId);
    }

    [Fact]
    public async Task New_intake_sends_the_ticket_number_back_on_whatsapp()
    {
        _queue.Due.Add(Job(Guid.NewGuid(), "login error", Now) with { ThreadKey = "107589064999080@lid" });

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        var ack = Assert.Single(_queue.Acknowledgements);
        Assert.Equal(ChannelType.WhatsApp, ack.Channel);
        Assert.Equal("107589064999080@lid", ack.Recipient); // the chat, not the phone number
        Assert.Equal("plane-intake:created", ack.Reason);
        Assert.StartsWith("Hello Numair,\n\nThank you for contacting us. We have received your request", ack.Body);
        Assert.Contains("Your ticket number is PLANE-12.", ack.Body); // FakePlane returns sequence 12
        Assert.EndsWith("— Intellifer Support", ack.Body);
    }

    [Fact]
    public void Email_acknowledgement_replies_in_the_thread_with_the_ticket_in_the_subject_fallback()
    {
        var job = Job(Guid.NewGuid(), "app error", Now) with
        {
            Channel = ChannelType.Email,
            Sender = "asha@example.com",
            ThreadKey = "thread-9",
            Subject = "Login error",
            CustomerName = "Asha"
        };

        var ack = PlaneIntakeDispatcher.Acknowledgement(job, PlaneInf, new PlaneIntakeCreated(Guid.NewGuid(), Guid.NewGuid(), 10), new PlaneOptions());

        Assert.Equal(("asha@example.com", "thread-9", "Re: Login error"), (ack.Recipient, ack.ThreadKey, ack.Subject));
        Assert.Contains("Your ticket number is PLANE-10.", ack.Body);
        Assert.Contains("Regards,\nIntellifer Support", ack.Body);

        var noSubject = PlaneIntakeDispatcher.Acknowledgement(job with { Subject = null }, PlaneInf, new PlaneIntakeCreated(Guid.NewGuid(), Guid.NewGuid(), 10), new PlaneOptions());
        Assert.Equal("We have received your request (PLANE-10)", noSubject.Subject);
    }

    [Fact]
    public async Task Acknowledgement_can_be_turned_off()
    {
        _queue.Due.Add(Job(Guid.NewGuid(), "login error", Now));

        await Dispatcher(acknowledge: false).RunOnceAsync(CancellationToken.None);

        Assert.Single(_plane.Created);
        Assert.Empty(_queue.Acknowledgements);
    }

    [Fact]
    public async Task Follow_up_comments_send_no_acknowledgement()
    {
        _queue.Due.Add(Job(Guid.NewGuid(), "still broken", Now, Guid.NewGuid()) with { PlaneProjectId = PlaneInf.Id });

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Empty(_queue.Acknowledgements);
    }

    [Theory]
    [InlineData("LT Form IV-D", null)]   // name, ignoring case, spaces and dashes
    [InlineData("Larsen valves", "LNT")] // Odoo code = Plane identifier
    public async Task Odoo_name_or_code_finds_the_plane_project(string name, string? code)
    {
        _customers.Result = Contact(name, code);
        _queue.Due.Add(Job(Guid.NewGuid(), "upload failed", Now));

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        var expected = code is null ? LtForm.Id : LtValves.Id;
        Assert.Equal(expected, Assert.Single(_plane.Created).ProjectId);
    }

    [Fact]
    public async Task Project_map_links_names_that_differ()
    {
        _customers.Result = Contact("Larsen Valves Pvt");
        _queue.Due.Add(Job(Guid.NewGuid(), "crash", Now));

        await Dispatcher(projectMap: "Larsen Valves Pvt=LNT").RunOnceAsync(CancellationToken.None);

        Assert.Equal(LtValves.Id, Assert.Single(_plane.Created).ProjectId);
    }

    [Fact]
    public async Task Contact_of_a_project_with_no_plane_project_is_skipped()
    {
        _customers.Result = Contact("testproj");
        var conversation = Guid.NewGuid();
        _queue.Due.AddRange([Job(conversation, "app error", Now.AddMinutes(-1)), Job(conversation, "still failing", Now)]);

        Assert.Equal(0, await Dispatcher().RunOnceAsync(CancellationToken.None));

        Assert.Empty(_plane.Created);
        Assert.Equal(2, _queue.Skipped.Count);
        Assert.Contains("testproj, which has no matching Plane project", _queue.Skipped[0].Reason);
        Assert.Equal(1, _customers.Lookups); // asked Odoo once for the conversation
    }

    [Fact]
    public async Task Plane_project_with_intake_off_is_skipped()
    {
        _customers.Result = Contact("Crane");
        _queue.Due.Add(Job(Guid.NewGuid(), "bug", Now));

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Contains("Intake is off in Plane project Crane", Assert.Single(_queue.Skipped).Reason);
    }

    [Fact]
    public async Task Unknown_sender_is_skipped()
    {
        _customers.Result = null;
        _queue.Due.Add(Job(Guid.NewGuid(), "bug", Now));

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Contains("not an active client contact", Assert.Single(_queue.Skipped).Reason);
        Assert.Empty(_plane.Created);
    }

    [Fact]
    public async Task Odoo_down_retries_instead_of_skipping()
    {
        _customers.Failure = new HttpRequestException("Network is unreachable");
        _queue.Due.Add(Job(Guid.NewGuid(), "error 500", Now));

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Empty(_queue.Skipped);
        Assert.Equal(Now.AddSeconds(30), Assert.Single(_queue.Failed).RetryAt);
    }

    [Fact]
    public async Task A_burst_of_messages_becomes_one_ticket_titled_by_the_problem()
    {
        var conversation = Guid.NewGuid();
        var hello = Job(conversation, "Hi", Now.AddMinutes(-4));
        var problem = Job(conversation, "Upload error please fix", Now.AddMinutes(-3)) with { HasMedia = true, MediaSummary = "image" };
        var more = Job(conversation, "It started today", Now.AddMinutes(-2));
        _queue.Due.AddRange([more, hello, problem]);

        Assert.Equal(3, await Dispatcher().RunOnceAsync(CancellationToken.None));

        var created = Assert.Single(_plane.Created);
        Assert.StartsWith("[WhatsApp] Numair: Upload error please fix", created.Title);
        Assert.True(created.Html.IndexOf("Hi") < created.Html.IndexOf("Upload error") && created.Html.IndexOf("Upload error") < created.Html.IndexOf("It started today"));
        Assert.Equal([hello.MessageId, problem.MessageId, more.MessageId], Assert.Single(_queue.IntakeCreated).MessageIds);
        Assert.Single(_queue.Acknowledgements);
        Assert.Empty(_plane.Comments);
    }

    [Fact]
    public async Task Open_recent_ticket_gets_the_new_messages_as_comments()
    {
        var ticket = Guid.NewGuid();
        _queue.Due.Add(Job(Guid.NewGuid(), "there is an error here", Now, ticket) with
        {
            PlaneProjectId = PlaneInf.Id,
            TicketLastMessageAt = Now.AddMinutes(-20)
        });

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Empty(_plane.Created);
        Assert.Equal(ticket, Assert.Single(_plane.Comments).WorkItemId);
    }

    [Fact]
    public async Task Closed_ticket_means_the_next_report_opens_a_new_one()
    {
        _queue.Due.Add(Job(Guid.NewGuid(), "there is an error here", Now, Guid.NewGuid()) with
        {
            PlaneProjectId = PlaneInf.Id,
            PlaneClosed = true,
            TicketLastMessageAt = Now.AddMinutes(-20)
        });

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Single(_plane.Created);
        Assert.Empty(_plane.Comments);
        Assert.Single(_queue.Acknowledgements);
    }

    [Fact]
    public async Task Idle_ticket_means_the_next_report_opens_a_new_one()
    {
        _queue.Due.Add(Job(Guid.NewGuid(), "upload failed again", Now, Guid.NewGuid()) with
        {
            PlaneProjectId = PlaneInf.Id,
            TicketLastMessageAt = Now.AddHours(-25)
        });

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Single(_plane.Created);
    }

    [Fact]
    public async Task Thanks_after_a_closed_ticket_is_skipped_not_a_new_ticket()
    {
        _queue.Due.Add(Job(Guid.NewGuid(), "Thanks, all good now!", Now, Guid.NewGuid()) with { PlaneClosed = true });

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Empty(_plane.Created);
        Assert.Empty(_plane.Comments);
        Assert.Single(_queue.Skipped);
    }

    [Fact]
    public async Task Waits_while_the_customer_is_still_writing()
    {
        var conversation = Guid.NewGuid();
        _queue.Due.Add(Job(conversation, "upload error", Now.AddSeconds(-30)) with { ConversationLastInboundAt = Now.AddSeconds(-30) });

        Assert.Equal(0, await Dispatcher(quietSeconds: 120).RunOnceAsync(CancellationToken.None));

        Assert.Empty(_plane.Created);
        Assert.Empty(_queue.Skipped);
        Assert.Equal(0, _customers.Lookups);
    }

    [Fact]
    public async Task Opens_once_the_conversation_has_been_quiet_long_enough()
    {
        _queue.Due.Add(Job(Guid.NewGuid(), "upload error", Now.AddMinutes(-3)) with { ConversationLastInboundAt = Now.AddMinutes(-3) });

        await Dispatcher(quietSeconds: 120).RunOnceAsync(CancellationToken.None);

        Assert.Single(_plane.Created);
    }

    [Fact]
    public async Task Burst_without_bug_wording_is_skipped_without_asking_odoo()
    {
        var conversation = Guid.NewGuid();
        _queue.Due.AddRange([Job(conversation, "Hi", Now.AddMinutes(-3)), Job(conversation, "What are your hours?", Now.AddMinutes(-2))]);

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Empty(_plane.Created);
        Assert.Equal(2, _queue.Skipped.Count);
        Assert.All(_queue.Skipped, skipped => Assert.Equal("No bug or error wording in these messages.", skipped.Reason));
        Assert.Equal(0, _customers.Lookups);
    }

    [Fact]
    public async Task Wording_split_across_messages_still_counts()
    {
        var conversation = Guid.NewGuid();
        _queue.Due.AddRange([Job(conversation, "the payment page is not", Now.AddMinutes(-3)), Job(conversation, "working", Now.AddMinutes(-3))]);

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Single(_plane.Created);
    }

    [Fact]
    public async Task Follow_up_comments_use_the_conversation_project_without_odoo()
    {
        var workItem = Guid.NewGuid();
        _queue.Due.Add(Job(Guid.NewGuid(), "still broken", Now, workItem) with { PlaneProjectId = LtValves.Id });

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Empty(_plane.Created);
        var comment = Assert.Single(_plane.Comments);
        Assert.Equal((LtValves.Id, workItem), (comment.ProjectId, comment.WorkItemId));
        Assert.Equal(0, _customers.Lookups);
    }

    [Fact]
    public async Task Older_conversations_comment_in_the_default_project()
    {
        _queue.Due.Add(Job(Guid.NewGuid(), "any update?", Now, Guid.NewGuid()));

        await Dispatcher(defaultProject: PlaneInf.Id).RunOnceAsync(CancellationToken.None);

        Assert.Equal(PlaneInf.Id, Assert.Single(_plane.Comments).ProjectId);
    }

    [Fact]
    public async Task Failure_backs_off_and_holds_later_messages_of_the_conversation()
    {
        var conversation = Guid.NewGuid();
        var first = Job(conversation, "error 500", Now.AddMinutes(-2));
        _queue.Due.AddRange([first, Job(conversation, "any update?", Now.AddMinutes(-1))]);
        _plane.Failure = new HttpRequestException("Connection refused");

        Assert.Equal(0, await Dispatcher().RunOnceAsync(CancellationToken.None));

        var failure = Assert.Single(_queue.Failed);
        Assert.Equal(first.MessageId, failure.MessageId);
        Assert.Equal(Now.AddSeconds(30), failure.RetryAt);
        Assert.Empty(_plane.Comments);
    }

    [Fact]
    public async Task A_timeout_is_retried_not_treated_as_shutdown()
    {
        _queue.Due.Add(Job(Guid.NewGuid(), "error 500", Now));
        _plane.Failure = new TaskCanceledException("The request timed out.");

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Equal(Now.AddSeconds(30), Assert.Single(_queue.Failed).RetryAt);
    }

    [Fact]
    public async Task Gives_up_after_the_last_attempt()
    {
        _queue.Due.Add(Job(Guid.NewGuid(), "bug", Now) with { Attempts = 7 });
        _plane.Failure = new HttpRequestException("Plane returned 404");

        await Dispatcher(maxAttempts: 8).RunOnceAsync(CancellationToken.None);

        Assert.Null(Assert.Single(_queue.Failed).RetryAt);
    }

    [Fact]
    public void Customer_text_is_html_encoded()
    {
        var job = Job(Guid.NewGuid(), "<script>alert(1)</script>\nline two", Now) with { CustomerName = "Asha & Co" };

        var html = PlaneIntakeDispatcher.Description(job);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Asha &amp; Co", html);
        Assert.Contains("<br>line two", html);
        Assert.StartsWith("[WhatsApp] Asha & Co: <script>", PlaneIntakeDispatcher.Title(job));
    }

    private static CustomerMatch Contact(string project, string? code = null) =>
        new(2, "Numair", "Intellifer", [new ClientProject(project, code)]);

    private PlaneIntakeDispatcher Dispatcher(
        int maxAttempts = 8,
        string? projectMap = null,
        Guid? defaultProject = null,
        bool acknowledge = true,
        int quietSeconds = 0)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PlaneOptions
        {
            AcknowledgeIntake = acknowledge,
            WhatsAppQuietSeconds = quietSeconds,
            MaxAttempts = maxAttempts,
            WorkspaceSlug = "planeinf",
            ProjectMap = projectMap,
            ProjectId = defaultProject?.ToString()
        });
        var clock = new FixedClock();
        return new PlaneIntakeDispatcher(
            _queue,
            _plane,
            _customers,
            new PlaneProjectResolver(_plane, options, clock),
            new IssueClassifier(options),
            options,
            clock,
            NullLogger<PlaneIntakeDispatcher>.Instance);
    }

    private static PlaneJob Job(Guid conversation, string body, DateTimeOffset receivedAt, Guid? workItem = null) => new(
        Guid.NewGuid(),
        conversation,
        ChannelType.WhatsApp,
        "+919876543210",
        null,
        null,
        null,
        body,
        false,
        null,
        receivedAt,
        0,
        workItem);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeQueue : IPlaneQueue
    {
        public List<PlaneJob> Due { get; } = [];
        public List<(IReadOnlyList<Guid> MessageIds, Guid ProjectId)> IntakeCreated { get; } = [];
        public List<OutboundDraft> Acknowledgements { get; } = [];
        public List<Guid> Sent { get; } = [];
        public List<(Guid MessageId, DateTimeOffset? RetryAt)> Failed { get; } = [];
        public List<(Guid MessageId, string Reason)> Skipped { get; } = [];

        public Task<IReadOnlyList<PlaneJob>> GetDueAsync(int take, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PlaneJob>>(Due.Take(take).ToList());

        public Task MarkIntakeCreatedAsync(
            IReadOnlyList<Guid> messageIds,
            Guid conversationId,
            Guid projectId,
            PlaneIntakeCreated created,
            DateTimeOffset now,
            OutboundDraft? acknowledgement,
            CancellationToken cancellationToken)
        {
            IntakeCreated.Add((messageIds, projectId));
            if (acknowledgement is not null)
                Acknowledgements.Add(acknowledgement);
            return Task.CompletedTask;
        }

        public Task MarkSentAsync(Guid messageId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Sent.Add(messageId);
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(Guid messageId, string error, DateTimeOffset? retryAt, CancellationToken cancellationToken)
        {
            Failed.Add((messageId, retryAt));
            return Task.CompletedTask;
        }

        public Task MarkSkippedAsync(Guid messageId, string reason, CancellationToken cancellationToken)
        {
            Skipped.Add((messageId, reason));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDirectory : ICustomerDirectory
    {
        public CustomerMatch? Result { get; set; }
        public Exception? Failure { get; set; }
        public int Lookups { get; private set; }
        public bool IsConfigured => true;

        public Task<CustomerMatch?> FindAsync(CustomerKey key, CancellationToken cancellationToken)
        {
            Lookups++;
            return Failure is null ? Task.FromResult(Result) : Task.FromException<CustomerMatch?>(Failure);
        }

        public Task<IReadOnlyList<ClientProject>> ListProjectsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClientProject>>([]);
    }

    private sealed class FakePlane(IReadOnlyList<PlaneProject> projects) : IPlaneGateway
    {
        public Guid WorkItemId { get; } = Guid.NewGuid();
        public Exception? Failure { get; set; }
        public List<(Guid ProjectId, string Title, string Html)> Created { get; } = [];
        public List<(Guid ProjectId, Guid WorkItemId, string Html)> Comments { get; } = [];

        public bool IsConfigured => true;

        public Task<IReadOnlyList<PlaneProject>> ListProjectsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(projects);

        public Task<PlaneIntakeCreated> CreateIntakeAsync(Guid projectId, string title, string descriptionHtml, CancellationToken cancellationToken)
        {
            if (Failure is not null)
                return Task.FromException<PlaneIntakeCreated>(Failure);
            Created.Add((projectId, title, descriptionHtml));
            return Task.FromResult(new PlaneIntakeCreated(Guid.NewGuid(), WorkItemId, 12));
        }

        public Task<Guid> UploadAttachmentAsync(Guid projectId, Guid workItemId, string fileName, string contentType, byte[] data, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PlaneItemStatus> GetIntakeStatusAsync(Guid projectId, Guid workItemId, CancellationToken cancellationToken) =>
            Task.FromResult(new PlaneItemStatus(PlaneItemStatus.Pending, "Triage", "triage"));

        public Task AddCommentAsync(Guid projectId, Guid workItemId, string commentHtml, string externalId, CancellationToken cancellationToken)
        {
            if (Failure is not null)
                return Task.FromException(Failure);
            Comments.Add((projectId, workItemId, commentHtml));
            return Task.CompletedTask;
        }
    }
}
