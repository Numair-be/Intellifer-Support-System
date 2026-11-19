using Intellifer.Hub.Application;
using Intellifer.Hub.Domain;
using Intellifer.Hub.Infrastructure.Gmail;

namespace Intellifer.Hub.Infrastructure.Replies;

/// <summary>Replies by email, in the customer's original Gmail thread.</summary>
public sealed class GmailReplySender(GmailMailboxClient gmail) : IReplySender
{
    public ChannelType Channel => ChannelType.Email;

    public bool IsConfigured => gmail.IsConfigured;

    public Task<string?> SendAsync(OutboundJob reply, CancellationToken cancellationToken) =>
        gmail.SendReplyAsync(reply.ThreadKey, reply.Recipient, reply.Subject ?? "Update on your request", reply.Body, cancellationToken);
}
