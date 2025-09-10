using MediatR;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;
using Percolator.Chat.Primitives;

namespace Percolator.Chat.App.Commands
{
    public record ReceiveKeyDistributionCommand(
        ConversationLookupKey Lookup,
        GroupKeyVersion KeyVersion,
        EncryptedGroupKey EncryptedKey
    ) : IRequest;
}
