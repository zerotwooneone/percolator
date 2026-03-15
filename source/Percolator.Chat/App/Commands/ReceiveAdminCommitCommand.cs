using MediatR;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App.Commands
{
    public record ReceiveAdminCommitCommand(
        ConversationLookupKey Lookup,
        Guid OpId,
        GroupKeyVersion CommittedKeyVersion,
        DateTimeOffset SentUtc,
        ulong AdminSequenceNumber,
        byte[] Signature
    ) : IRequest;
}
