using Intellifer.Hub.Application;
using Intellifer.Hub.Application.Options;

namespace Intellifer.Hub.Application.Tests;

public sealed class IssueClassifierTests
{
    private readonly IssueClassifier _classifier = new(Microsoft.Extensions.Options.Options.Create(new PlaneOptions()));

    [Theory]
    [InlineData("Login page throws an error", null)]
    [InlineData(null, "The app keeps CRASHING when I open reports")]
    [InlineData("Invoice", "Payment page is not   working since morning")]
    [InlineData(null, "it doesn’t work on my phone")]
    [InlineData("Found a bug", "")]
    [InlineData(null, "Upload failed again")]
    [InlineData(null, "Fix this new one")]
    [InlineData("Please fix", "the export page")]
    public void Bug_and_error_reports_are_flagged(string? subject, string? body)
    {
        Assert.True(_classifier.IsIssue(subject, body));
    }

    [Theory]
    [InlineData("Quote request", "Please send pricing for 20 seats")]
    [InlineData(null, "Thanks, all good now!")]
    [InlineData(null, "Watching a terrorism documentary")] // "error" inside another word
    [InlineData(null, "Ladybugs in the garden")]
    [InlineData(null, "It is fixed now, thanks")] // "fixed" is not "fix"
    [InlineData(null, "We need a prefix for the invoice numbers")]
    public void Ordinary_messages_are_not_flagged(string? subject, string? body)
    {
        Assert.False(_classifier.IsIssue(subject, body));
    }

    [Fact]
    public void Keywords_come_from_settings()
    {
        var classifier = new IssueClassifier(Microsoft.Extensions.Options.Options.Create(
            new PlaneOptions { IntakeKeywords = "outage, payment declined" }));

        Assert.True(classifier.IsIssue(null, "Payment  declined at checkout"));
        Assert.False(classifier.IsIssue(null, "There is an error"));
    }
}
