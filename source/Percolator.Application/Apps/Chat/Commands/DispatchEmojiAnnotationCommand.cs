using MediatR;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Apps.Chat;

public sealed record DispatchEmojiAnnotationCommand(
    Guid MessageId,
    string Emoji,
    DateTime SentTimestampUtc,
    IReadOnlyList<PeerId> RecipientPeerIds,
    PeerId SenderPeerId
) : IRequest;
