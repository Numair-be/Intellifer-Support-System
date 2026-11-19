using Intellifer.Hub.Application;
using Microsoft.EntityFrameworkCore;

namespace Intellifer.Hub.Infrastructure.Persistence;

public sealed class InboundQuery(HubDbContext db) : IInboundQuery
{
    public async Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(int take, CancellationToken cancellationToken)
    {
        return await db.Conversations
            .OrderByDescending(conversation => conversation.LastInboundAt)
            .Take(take)
            .Select(conversation => new ConversationSummary(
                conversation.Id,
                conversation.Channel,
                conversation.ThreadKey,
                conversation.ParticipantNormalized,
                conversation.Subject,
                conversation.LastInboundAt,
                conversation.Messages.Count,
                conversation.CustomerName,
                conversation.CustomerCompany,
                conversation.CustomerProject,
                conversation.CustomerContactId,
                conversation.PlaneWorkItemId,
                conversation.PlaneSequenceId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MessageSummary>?> ListMessagesAsync(
        Guid conversationId,
        int take,
        CancellationToken cancellationToken)
    {
        var exists = await db.Conversations.AnyAsync(conversation => conversation.Id == conversationId, cancellationToken);
        if (!exists)
            return null;

        return await db.Messages
            .Where(message => message.ConversationId == conversationId)
            .OrderBy(message => message.ReceivedAt)
            .Take(take)
            .Select(message => new MessageSummary(
                message.Id,
                message.ExternalMessageId,
                message.SenderNormalized,
                message.Subject,
                message.Body,
                message.HasMedia,
                message.MediaSummary,
                message.ReceivedAt))
            .ToListAsync(cancellationToken);
    }
}
