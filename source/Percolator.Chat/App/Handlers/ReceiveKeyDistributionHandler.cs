using MediatR;
using Percolator.Chat.App.Commands;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Chat.App.Handlers
{
    // On recipient, import the encrypted group key via Application adapter (IGroupKeyOperations)
    public class ReceiveKeyDistributionHandler : IRequestHandler<ReceiveKeyDistributionCommand>
    {
        private readonly IConversationResolver _resolver;
        private readonly IGroupKeyOperations _groupKeyOps;

        public ReceiveKeyDistributionHandler(IConversationResolver resolver, IGroupKeyOperations groupKeyOps)
        {
            _resolver = resolver;
            _groupKeyOps = groupKeyOps;
        }

        public async Task Handle(ReceiveKeyDistributionCommand request, CancellationToken cancellationToken)
        {
            request.Lookup.EnsureExactlyOne();
            var resolution = await _resolver.ResolveAsync(request.Lookup, cancellationToken);
            var conversationId = resolution.Conversation.Id.Value;

            await _groupKeyOps.ImportGroupKeyAsync(conversationId, request.KeyVersion, request.EncryptedKey, cancellationToken);
        }
    }
}
