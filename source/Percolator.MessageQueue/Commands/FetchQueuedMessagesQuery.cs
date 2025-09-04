using MediatR;
using Percolator.Identity;
using Percolator.MessageQueue.Results;

namespace Percolator.MessageQueue.Commands;

public record FetchQueuedMessagesQuery(
    PeerId RecipientPeerId,
    int MaxCount
) : IRequest<FetchQueuedMessagesResult>;
