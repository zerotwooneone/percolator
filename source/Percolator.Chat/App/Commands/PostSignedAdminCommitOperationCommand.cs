using MediatR;

namespace Percolator.Chat.App.Commands;

public sealed record PostSignedAdminCommitOperationCommand(
    ConversationLookupKey Lookup,
    Guid OpId,
    uint CommittedKeyVersion,
    DateTimeOffset SentUtc,
    byte[] Signature,
    ulong? AdminSequenceNumber
) : IRequest;
