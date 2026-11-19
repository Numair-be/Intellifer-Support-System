using Intellifer.Hub.Application;
using Intellifer.Hub.Application.Options;
using Intellifer.Hub.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Intellifer.Hub.Application.Tests;

public sealed class SenderNormalizerTests
{
    private readonly SenderNormalizer _normalizer = new(Microsoft.Extensions.Options.Options.Create(new HubOptions { DefaultPhoneRegion = "IN" }));

    [Fact]
    public void Email_strips_display_name_and_lowercases()
    {
        var normalized = _normalizer.Normalize(ChannelType.Email, "Ada Lovelace <Ada@Example.com>");
        Assert.Equal("ada@example.com", normalized);
    }

    [Fact]
    public void WhatsApp_phone_becomes_e164()
    {
        var normalized = _normalizer.Normalize(ChannelType.WhatsApp, "919876543210@c.us");
        Assert.Equal("+919876543210", normalized);
    }

    [Fact]
    public void WhatsApp_group_id_stays_intact()
    {
        var normalized = _normalizer.Normalize(ChannelType.WhatsApp, "1203630@g.us");
        Assert.Equal("1203630@g.us", normalized);
    }
}
