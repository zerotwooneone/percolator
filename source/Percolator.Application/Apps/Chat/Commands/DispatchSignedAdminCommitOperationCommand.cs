using MediatR;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Apps.Chat;

public sealed record DispatchSignedAdminCommitOperationCommand(
    Guid GroupConversationId,
    Guid OpId,
    uint CommittedKeyVersion,
    DateTime SentTimestampUtc,
    IReadOnlyList<PeerId> RecipientPeerIds,
    PeerId SenderPeerId,
    byte[] Signature,
    ulong? AdminSequenceNumber
) : IRequest;
