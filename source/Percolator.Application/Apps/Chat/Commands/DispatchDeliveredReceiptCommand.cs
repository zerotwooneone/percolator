using MediatR;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Apps.Chat.Commands;

public sealed record DispatchDeliveredReceiptCommand(
    Guid MessageId,
    DateTime SentTimestampUtc,
    IReadOnlyList<PeerId> RecipientPeerIds,
    PeerId SenderPeerId
) : IRequest;
