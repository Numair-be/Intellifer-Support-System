using Intellifer.Hub.Domain;

namespace Intellifer.Hub.Application;

public sealed record ConversationSummary(
    Guid Id,
    ChannelType Channel,
    string ThreadKey,
    string Participant,
    string? Subject,
    DateTimeOffset LastInboundAt,
    int MessageCount,
    string? CustomerName,
    string? CustomerCompany,
    string? CustomerProject,
    int? CustomerContactId,
    Guid? PlaneWorkItemId,
    int? PlaneSequenceId);

public sealed record MessageSummary(
    Guid Id,
    string ExternalMessageId,
    string Sender,
    string? Subject,
    string Body,
    bool HasMedia,
    string? MediaSummary,
    DateTimeOffset ReceivedAt);

public interface IInboundQuery
{
    Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<MessageSummary>?> ListMessagesAsync(Guid conversationId, int take, CancellationToken cancellationToken);
}
