using System.Text.Json;
using Intellifer.Hub.Application;
using Intellifer.Hub.Domain;

namespace Intellifer.Hub.Application.Tests;

/// <summary>Webhooks from OpenWA by rmyndharis: { event, sessionId, idempotencyKey, data }.</summary>
public sealed class OpenWaEventInterpreterTests
{
    private readonly OpenWaEventInterpreter _interpreter = new();

    private OpenWaInterpretation Interpret(string json)
    {
        using var document = JsonDocument.Parse(json);
        return _interpreter.Interpret(document.RootElement);
    }

    [Fact]
    public void Message_received_maps_sender_thread_and_text()
    {
        var result = Assert.IsType<OpenWaInterpretation.Inbound>(Interpret("""
            {
              "event": "message.received",
              "sessionId": "8f3c2b1a",
              "idempotencyKey": "msg_8f3c2b1a_ABC123",
              "data": {
                "id": "ABC123",
                "from": "919876543210@c.us",
                "to": "919000000000@c.us",
                "chatId": "919876543210@c.us",
                "body": "Need help with an order",
                "type": "text",
                "timestamp": 1700000000,
                "fromMe": false,
                "isGroup": false
              }
            }
            """));

        Assert.Equal(ChannelType.WhatsApp, result.Message.Channel);
        Assert.Equal("ABC123", result.Message.ExternalMessageId);
        Assert.Equal("919876543210@c.us", result.Message.ThreadKey);
        Assert.Equal("919876543210@c.us", result.Message.SenderRaw);
        Assert.Equal("Need help with an order", result.Message.Body);
        Assert.False(result.Message.HasMedia);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), result.Message.ReceivedAt);
    }

    [Fact]
    public void Own_messages_are_ignored()
    {
        var result = Assert.IsType<OpenWaInterpretation.Ignored>(Interpret("""
            {"event":"message.received","data":{"id":"1","from":"9198@c.us","fromMe":true,"body":"ack"}}
            """));
        Assert.Equal("message was sent by the gateway", result.Reason);
    }

    [Fact]
    public void Group_message_uses_the_group_as_thread_and_the_author_as_sender()
    {
        var result = Assert.IsType<OpenWaInterpretation.Inbound>(Interpret("""
            {"event":"message.received","data":{"id":"g1","from":"1203630@g.us","chatId":"1203630@g.us",
             "author":"919876543210@c.us","body":"hello","isGroup":true,"type":"text"}}
            """));
        Assert.Equal("1203630@g.us", result.Message.ThreadKey);
        Assert.Equal("919876543210@c.us", result.Message.SenderRaw);
    }

    [Fact]
    public void Lid_sender_uses_the_resolved_phone_number()
    {
        var result = Assert.IsType<OpenWaInterpretation.Inbound>(Interpret("""
            {"event":"message.received","data":{"id":"l1","from":"107589064999080@lid","chatId":"107589064999080@lid",
             "senderPhone":"919876543210","body":"upload error","type":"text"}}
            """));
        Assert.Equal("107589064999080@lid", result.Message.ThreadKey); // replies go to the chat
        Assert.Equal("919876543210@c.us", result.Message.SenderRaw);
    }

    [Fact]
    public void Lid_sender_without_a_phone_number_keeps_the_lid()
    {
        var result = Assert.IsType<OpenWaInterpretation.Inbound>(Interpret("""
            {"event":"message.received","data":{"id":"l2","from":"107589064999080@lid","body":"hi","type":"text"}}
            """));
        Assert.Equal("107589064999080@lid", result.Message.SenderRaw);
    }

    [Fact]
    public void Small_image_arrives_inline_with_its_caption()
    {
        var png = Convert.ToBase64String(new byte[] { 137, 80, 78, 71, 1, 2, 3 });
        var result = Assert.IsType<OpenWaInterpretation.Inbound>(Interpret("""
            {"event":"message.received","data":{"id":"img1","from":"919876543210@c.us","chatId":"919876543210@c.us",
             "type":"image","body":"Upload page shows an error","timestamp":1763542378,
             "media":{"mimetype":"image/png","data":"PNG_DATA","sizeBytes":7}}}
            """.Replace("PNG_DATA", png)));

        Assert.Equal("Upload page shows an error", result.Message.Body);
        Assert.True(result.Message.HasMedia);
        var image = Assert.Single(result.Message.Attachments);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(7, image.Data!.Length);
        Assert.StartsWith("whatsapp-image-", image.FileName);
        Assert.EndsWith(".png", image.FileName);
    }

    [Fact]
    public void Large_image_is_recorded_for_download()
    {
        var result = Assert.IsType<OpenWaInterpretation.Inbound>(Interpret("""
            {"event":"message.received","data":{"id":"img2","from":"919876543210@c.us","chatId":"919876543210@c.us",
             "type":"image","media":{"mimetype":"image/jpeg","filename":"screen.jpg","omitted":true,"sizeBytes":2500000}}}
            """));

        var image = Assert.Single(result.Message.Attachments);
        Assert.Null(image.Data);
        Assert.Equal(("screen.jpg", 2500000L), (image.FileName, image.Size));
        Assert.Equal("""{"chatId":"919876543210@c.us","messageId":"img2"}""", image.Source);
    }

    [Fact]
    public void Stickers_and_documents_are_not_image_attachments()
    {
        var sticker = Assert.IsType<OpenWaInterpretation.Inbound>(Interpret("""
            {"event":"message.received","data":{"id":"s1","from":"919876543210@c.us","type":"sticker","media":{"mimetype":"image/webp"}}}
            """));
        var pdf = Assert.IsType<OpenWaInterpretation.Inbound>(Interpret("""
            {"event":"message.received","data":{"id":"d1","from":"919876543210@c.us","type":"document","media":{"mimetype":"application/pdf","filename":"bill.pdf"}}}
            """));

        Assert.Empty(sticker.Message.Attachments);
        Assert.Empty(pdf.Message.Attachments);
        Assert.Equal("document, application/pdf, bill.pdf", pdf.Message.MediaSummary);
    }

    [Theory]
    [InlineData("ready", "ok")]
    [InlineData("qr_ready", "attention")]
    [InlineData("disconnected", "attention")]
    public void Session_status_feeds_channel_health(string status, string expected)
    {
        var result = Assert.IsType<OpenWaInterpretation.Session>(Interpret("""
            {"event":"session.status","data":{"status":"STATUS"}}
            """.Replace("STATUS", status)));
        Assert.Equal(expected, result.Status);
        Assert.Equal(status, result.Detail);
    }

    [Fact]
    public void Disconnect_is_attention_with_the_reason()
    {
        var result = Assert.IsType<OpenWaInterpretation.Session>(Interpret("""
            {"event":"session.disconnected","data":{"reason":"LOGOUT"}}
            """));
        Assert.Equal("attention", result.Status);
        Assert.Contains("LOGOUT", result.Detail);
    }

    [Fact]
    public void Other_events_are_ignored()
    {
        Assert.IsType<OpenWaInterpretation.Ignored>(Interpret("""{"event":"message.ack","data":{"id":"x"}}"""));
        Assert.IsType<OpenWaInterpretation.Ignored>(Interpret("""{"event":"message.received"}"""));
    }
}
