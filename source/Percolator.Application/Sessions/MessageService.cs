using Percolator.Sessions;

namespace Percolator.Application.Sessions;

public class MessageService : IMessageService
{
    private readonly ILocalPeerProvider _localPeerProvider;
    private readonly IMessageStore _messageStore;

    public MessageService(ILocalPeerProvider localPeerProvider, IMessageStore messageStore)
    {
        _localPeerProvider = localPeerProvider;
        _messageStore = messageStore;
    }

    public async Task<DirectMessage> SendDirectMessageAsync(
        ConversationId conversationId,
        OpaqueContent content)
    {
        // The messageType parameter is currently unused. It will be used later
        // to construct an envelope for the opaque content.
        var senderId = await _localPeerProvider.GetPeerIdAsync();

        var message = new DirectMessage(
            MessageId.NewId(),
            conversationId,
            senderId,
            content
            );

        await _messageStore.StoreDirectMessageAsync(message);

        return message;
    }
}
