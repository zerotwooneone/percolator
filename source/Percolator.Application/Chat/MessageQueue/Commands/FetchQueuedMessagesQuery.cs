using MediatR;
using Percolator.Identity;
using Percolator.Application.Chat.MessageQueue.Results;

namespace Percolator.Application.Chat.MessageQueue.Commands;

public record FetchQueuedMessagesQuery(
    PeerId RecipientPeerId,
    int MaxCount
) : IRequest<FetchQueuedMessagesResult>;
