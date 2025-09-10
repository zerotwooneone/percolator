using MediatR;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;
using System;

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
