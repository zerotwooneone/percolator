using MediatR;
using Percolator.Chat.App.Commands;

namespace Percolator.Chat.App.Handlers
{
    // On acting admin, record which members have adopted the key version (Infrastructure persistence via IKeyAdoptionStore)
    public class ReceiveKeyAdoptionConfirmationHandler : IRequestHandler<ReceiveKeyAdoptionConfirmationCommand>
    {
        private readonly IConversationResolver _resolver;
        private readonly IKeyAdoptionStore _store;
        private readonly IMediator _mediator;

        public ReceiveKeyAdoptionConfirmationHandler(IConversationResolver resolver, IKeyAdoptionStore store, IMediator mediator)
        {
            _resolver = resolver;
            _store = store;
            _mediator = mediator;
        }

        public async Task Handle(ReceiveKeyAdoptionConfirmationCommand request, CancellationToken cancellationToken)
        {
            request.Lookup.EnsureExactlyOne();
            var resolution = await _resolver.ResolveAsync(request.Lookup, cancellationToken);
            var conversationId = resolution.Conversation.Id.Value;

            await _store.AddAsync(conversationId, request.KeyVersion, request.AdopterIdentity, request.SentUtc, request.Signature, cancellationToken);
            await _mediator.Publish(new KeyAdoptionStoredNotification(conversationId, request.KeyVersion), cancellationToken);
        }
    }
}
