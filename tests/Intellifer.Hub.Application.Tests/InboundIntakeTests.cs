using Intellifer.Hub.Application;
using Intellifer.Hub.Application.Options;
using Intellifer.Hub.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Intellifer.Hub.Application.Tests;

public sealed class InboundIntakeTests
{
    private readonly MemoryStore _store = new();
    private readonly FakeDirectory _customers = new();

    [Fact]
    public async Task Second_delivery_is_a_duplicate()
    {
        var intake = CreateIntake();

        var message = Sample();
        Assert.Equal(IntakeResult.Stored, await intake.AcceptAsync(message, CancellationToken.None));
        Assert.Equal(IntakeResult.Duplicate, await intake.AcceptAsync(message, CancellationToken.None));
        Assert.Equal("+919876543210", _store.Saved.Single().SenderNormalized);
        Assert.Equal(1, _customers.Lookups);
    }

    [Fact]
    public async Task Stored_message_is_linked_to_the_odoo_customer()
    {
        _customers.Result = new CustomerMatch(42, "Asha Rao", "Acme Pvt Ltd", [new ClientProject("planeinf")]);
        var intake = CreateIntake();

        await intake.AcceptAsync(Sample(), CancellationToken.None);

        Assert.Equal(new CustomerKey(CustomerKeyKind.Phone, "+919876543210"), _customers.LastKey);
        var (threadKey, match) = Assert.Single(_store.Customers);
        Assert.Equal("919876543210@c.us", threadKey);
        Assert.Equal(42, match?.ContactId);
    }

    [Fact]
    public async Task Odoo_failure_still_stores_the_message()
    {
        _customers.Failure = new HttpRequestException("Connection refused");
        var intake = CreateIntake();

        Assert.Equal(IntakeResult.Stored, await intake.AcceptAsync(Sample(), CancellationToken.None));
        Assert.Single(_store.Saved);
        Assert.Empty(_store.Customers);
    }

    [Fact]
    public async Task Lid_sender_is_not_looked_up()
    {
        var intake = CreateIntake();

        await intake.AcceptAsync(Sample() with { SenderRaw = "107589064999080@lid" }, CancellationToken.None);

        Assert.Equal(0, _customers.Lookups);
    }

    [Fact]
    public async Task Bug_report_is_flagged_for_plane_only_when_plane_is_set_up()
    {
        await CreateIntake(planeConfigured: false).AcceptAsync(Sample() with { Body = "app crashed" }, CancellationToken.None);
        await CreateIntake(planeConfigured: true).AcceptAsync(
            Sample() with { ExternalMessageId = "m2", Body = "app crashed" },
            CancellationToken.None);
        await CreateIntake(planeConfigured: true).AcceptAsync(
            Sample() with { ExternalMessageId = "m3", Body = "thanks!" },
            CancellationToken.None);

        Assert.Equal([false, true, false], _store.Saved.Select(message => message.FlaggedAsIssue));
        Assert.Equal([false, true, true], _store.Saved.Select(message => message.QueueForPlane));
    }

    private InboundIntake CreateIntake(bool planeConfigured = false)
    {
        var plane = planeConfigured
            ? new PlaneOptions { Url = "https://plane.test", ApiKey = "k", WorkspaceSlug = "w", ProjectId = Guid.NewGuid().ToString() }
            : new PlaneOptions();
        var planeOptions = Microsoft.Extensions.Options.Options.Create(plane);
        return new InboundIntake(
            _store,
            new SenderNormalizer(Microsoft.Extensions.Options.Options.Create(new HubOptions { DefaultPhoneRegion = "IN" })),
            _customers,
            new IssueClassifier(planeOptions),
            planeOptions,
            NullLogger<InboundIntake>.Instance);
    }

    private static InboundMessage Sample() => new()
    {
        Channel = ChannelType.WhatsApp,
        ExternalMessageId = "m1",
        ThreadKey = "919876543210@c.us",
        SenderRaw = "919876543210@c.us",
        SenderNormalized = "raw",
        Body = "hello",
        ReceivedAt = DateTimeOffset.UnixEpoch
    };

    private sealed class MemoryStore : IInboundStore
    {
        public List<InboundMessage> Saved { get; } = [];

        public List<(string ThreadKey, CustomerMatch? Match)> Customers { get; } = [];

        public Task<bool> SaveAsync(InboundMessage message, CancellationToken cancellationToken)
        {
            if (Saved.Any(saved => saved.Channel == message.Channel && saved.ExternalMessageId == message.ExternalMessageId))
                return Task.FromResult(false);
            Saved.Add(message);
            return Task.FromResult(true);
        }

        public Task SetCustomerAsync(
            ChannelType channel,
            string threadKey,
            CustomerMatch? match,
            DateTimeOffset checkedAt,
            CancellationToken cancellationToken)
        {
            Customers.Add((threadKey, match));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDirectory : ICustomerDirectory
    {
        public CustomerMatch? Result { get; set; }

        public Exception? Failure { get; set; }

        public int Lookups { get; private set; }

        public CustomerKey? LastKey { get; private set; }

        public bool IsConfigured => true;

        public Task<IReadOnlyList<ClientProject>> ListProjectsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClientProject>>([]);

        public Task<CustomerMatch?> FindAsync(CustomerKey key, CancellationToken cancellationToken)
        {
            Lookups++;
            LastKey = key;
            return Failure is null ? Task.FromResult(Result) : Task.FromException<CustomerMatch?>(Failure);
        }
    }
}
