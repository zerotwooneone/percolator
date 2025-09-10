using MediatR;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;
using System;

namespace Percolator.Chat.App.Commands
{
    public record ReceiveKeyAdoptionConfirmationCommand(
        ConversationLookupKey Lookup,
        GroupKeyVersion KeyVersion,
        IdentityPublicKey AdopterIdentity,
        DateTimeOffset SentUtc,
        byte[] Signature
    ) : IRequest;
}
