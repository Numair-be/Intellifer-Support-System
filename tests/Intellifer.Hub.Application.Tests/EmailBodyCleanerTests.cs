using Intellifer.Hub.Application;

namespace Intellifer.Hub.Application.Tests;

public sealed class EmailBodyCleanerTests
{
    [Fact]
    public void Clean_drops_quoted_reply_and_signature()
    {
        var raw = "The printer is offline.\n\n-- \nAda Lovelace\n\nOn Monday, Ada wrote:\n> previous";
        var cleaned = EmailBodyCleaner.Clean(raw);
        Assert.Equal("The printer is offline.", cleaned);
    }

    [Fact]
    public void Clean_drops_a_gmail_attribution_wrapped_over_two_lines()
    {
        var raw = "has it been fixed?\n\nOn Thu, Nov 19, 2026 at 1:00 PM Asha Rao <\nasha@example.com> wrote:\n> earlier text";

        Assert.Equal("has it been fixed?", EmailBodyCleaner.Clean(raw));
    }

    [Fact]
    public void Clean_keeps_ordinary_lines_that_start_with_on()
    {
        var raw = "On Monday the export failed.\nOn Tuesday it worked.";

        Assert.Equal(raw, EmailBodyCleaner.Clean(raw));
    }

    [Fact]
    public void ToPlain_keeps_line_breaks()
    {
        var plain = EmailBodyCleaner.ToPlain("<p>Hello</p><br>there");
        Assert.Contains("Hello", plain);
        Assert.Contains("there", plain);
        Assert.DoesNotContain("<", plain);
    }
}
