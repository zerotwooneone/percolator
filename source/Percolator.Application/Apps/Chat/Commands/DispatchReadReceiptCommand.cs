using MediatR;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Apps.Chat;

public sealed record DispatchReadReceiptCommand(
    Guid MessageId,
    DateTime SentTimestampUtc,
    IReadOnlyList<PeerId> RecipientPeerIds,
    PeerId SenderPeerId
) : IRequest;
