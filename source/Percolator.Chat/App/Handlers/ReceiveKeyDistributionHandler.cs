using MediatR;
using Percolator.Chat.App.Commands;

namespace Percolator.Chat.App.Handlers
{
    // On recipient, import the encrypted group key via Application service (IGroupSenderKeyService)
    public class ReceiveKeyDistributionHandler : IRequestHandler<ReceiveKeyDistributionCommand>
    {
        private readonly IConversationResolver _resolver;
        private readonly Percolator.Application.Apps.Chat.IGroupSenderKeyService _senderKeyService;
        
        public ReceiveKeyDistributionHandler(IConversationResolver resolver, Percolator.Application.Apps.Chat.IGroupSenderKeyService senderKeyService)
        {
            _resolver = resolver;
            _senderKeyService = senderKeyService;
        }

        public async Task Handle(ReceiveKeyDistributionCommand request, CancellationToken cancellationToken)
        {
            request.Lookup.EnsureExactlyOne();
            var resolution = await _resolver.ResolveAsync(request.Lookup, cancellationToken);
            var conversationId = resolution.Conversation.Id.Value;
            await _senderKeyService.ImportSenderKeyAsync(conversationId, request.KeyVersion, request.EncryptedKey, cancellationToken);
        }
    }
}
