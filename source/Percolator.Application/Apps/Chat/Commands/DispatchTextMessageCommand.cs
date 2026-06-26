using MediatR;
using Percolator.Identity;

namespace Percolator.Application.Apps.Chat.Commands;

public sealed record DispatchTextMessageCommand(
    Guid MessageId,
    string Content,
    DateTimeOffset SentTimestampUtc,
    IReadOnlyList<PeerId> RecipientPeerIds
) : IRequest;
