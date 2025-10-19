using MediatR;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Apps.Chat;

public sealed record DispatchDeliveredReceiptCommand(
    Guid MessageId,
    DateTime SentTimestampUtc,
    IReadOnlyList<PeerId> RecipientPeerIds,
    PeerId SenderPeerId
) : IRequest;
