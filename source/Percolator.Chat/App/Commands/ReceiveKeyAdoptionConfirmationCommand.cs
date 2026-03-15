using MediatR;
using Percolator.Chat.ValueObjects;

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
