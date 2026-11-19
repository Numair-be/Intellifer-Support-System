using Intellifer.Hub.Domain;

namespace Intellifer.Hub.Application;

/// <summary>Formats replies to customers the same way for every reason (ticket created, status changed).</summary>
public static class ReplyComposer
{
    /// <param name="participant">The customer's email address (used for email).</param>
    /// <param name="threadKey">The Gmail thread or WhatsApp chat to reply in.</param>
    /// <param name="message">The body, one or more sentences, without greeting or signature.</param>
    public static OutboundDraft Compose(
        Guid conversationId,
        ChannelType channel,
        string participant,
        string threadKey,
        string? customerName,
        string? subject,
        string message,
        string? signature,
        string reason,
        string fallbackSubject)
    {
        var greeting = string.IsNullOrWhiteSpace(customerName) ? "Hello," : $"Hello {customerName.Trim()},";
        var signedBy = string.IsNullOrWhiteSpace(signature) ? "Support" : signature.Trim();

        if (channel == ChannelType.Email)
        {
            var replySubject = string.IsNullOrWhiteSpace(subject)
                ? fallbackSubject
                : subject.Trim().StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? subject.Trim() : $"Re: {subject.Trim()}";
            var body = $"{greeting}\n\n{message}\n\n"
                + "You can reply to this email if you have anything to add.\n\n"
                + $"Regards,\n{signedBy}";
            return new OutboundDraft(conversationId, channel, participant, threadKey, replySubject, body, reason);
        }

        // WhatsApp: sent to the chat (which may be a LID), not the phone number.
        return new OutboundDraft(conversationId, channel, threadKey, threadKey, null, $"{greeting}\n\n{message}\n\n— {signedBy}", reason);
    }

    public static string TicketNumber(string? projectIdentifier, int? sequenceId) =>
        sequenceId is null ? "" : string.IsNullOrWhiteSpace(projectIdentifier) ? $"#{sequenceId}" : $"{projectIdentifier}-{sequenceId}";
}
