using Intellifer.Hub.Application;
using Intellifer.Hub.Application.Options;
using Intellifer.Hub.Domain;

namespace Intellifer.Hub.Application.Tests;

public sealed class CustomerMatcherTests
{
    private readonly CustomerMatcher _matcher = new(
        new SenderNormalizer(Microsoft.Extensions.Options.Options.Create(new HubOptions { DefaultPhoneRegion = "IN" })));

    [Theory]
    [InlineData("+91 98765 43210")]
    [InlineData("098765-43210")]
    [InlineData("9876543210")]
    [InlineData("+91-987-654-3210")]
    public void Phone_matches_whatever_format_odoo_stored(string stored)
    {
        var key = Key(ChannelType.WhatsApp, "+919876543210");
        var match = _matcher.Pick(key, [Candidate(7, "Asha Rao", phones: [stored])]);

        Assert.Equal(7, match?.ContactId);
        Assert.Equal("Asha Rao", match?.Name);
    }

    [Fact]
    public void Phone_with_the_same_last_digits_is_not_a_match()
    {
        var key = Key(ChannelType.WhatsApp, "+919876543210");
        Assert.Null(_matcher.Pick(key, [Candidate(7, "Someone Else", phones: ["+91 91234 43210"])]));
    }

    [Fact]
    public void Email_must_match_exactly_not_as_a_substring()
    {
        var key = Key(ChannelType.Email, "ann@acme.com");
        var candidates = new[]
        {
            Candidate(1, "Joann", emails: ["joann@acme.com"]),
            Candidate(2, "Ann Lee", emails: ["Ann Lee <ANN@acme.com>"])
        };

        Assert.Equal(2, _matcher.Pick(key, candidates)?.ContactId);
    }

    [Fact]
    public void Alternate_email_matches_too()
    {
        var key = Key(ChannelType.Email, "numair@work.example");
        var match = _matcher.Pick(key, [Candidate(2, "Numair", emails: ["numair@home.example", "numair@work.example"], project: new ClientProject("planeinf"))]);

        Assert.Equal(2, match?.ContactId);
        Assert.Equal("planeinf", Assert.Single(match!.Projects).Name);
    }

    [Fact]
    public void Contact_on_several_projects_lists_them_all()
    {
        var key = Key(ChannelType.Email, "ravi@acme.com");
        var candidates = new[]
        {
            Candidate(9, "Ravi Kumar", emails: ["ravi@acme.com"], project: new ClientProject("testproj"), company: "Acme"),
            Candidate(4, "Ravi K", emails: ["ravi@acme.com"], project: new ClientProject("planeinf", "PLANE"))
        };

        var match = _matcher.Pick(key, candidates)!;
        Assert.Equal(4, match.ContactId);
        Assert.Equal(["planeinf", "testproj"], match.Projects.Select(project => project.Name));
        Assert.Equal("PLANE", match.Projects[0].Code);
    }

    [Theory]
    [InlineData(ChannelType.WhatsApp, "107589064999080@lid")]
    [InlineData(ChannelType.WhatsApp, "1203630@g.us")]
    [InlineData(ChannelType.Email, "unknown")]
    public void Senders_without_a_phone_or_email_are_not_looked_up(ChannelType channel, string sender)
    {
        Assert.False(CustomerKey.TryCreate(channel, sender, out _));
    }

    private static CustomerKey Key(ChannelType channel, string sender)
    {
        Assert.True(CustomerKey.TryCreate(channel, sender, out var key));
        return key;
    }

    private static ContactCandidate Candidate(
        int id,
        string name,
        string[]? emails = null,
        string[]? phones = null,
        ClientProject? project = null,
        string? company = null) =>
        new(id, name, company, project, emails ?? [], phones ?? []);
}
