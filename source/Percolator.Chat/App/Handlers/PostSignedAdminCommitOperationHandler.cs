using MediatR;
using Percolator.Chat.App.Commands;
using Percolator.Chat.Events;

namespace Percolator.Chat.App.Handlers;

public sealed class PostSignedAdminCommitOperationHandler : IRequestHandler<PostSignedAdminCommitOperationCommand>
{
    private readonly IConversationResolver _resolver;
    private readonly IPublisher _publisher;

    public PostSignedAdminCommitOperationHandler(
        IConversationResolver resolver,
        IPublisher publisher)
    {
        _resolver = resolver;
        _publisher = publisher;
    }

    public async Task Handle(PostSignedAdminCommitOperationCommand request, CancellationToken cancellationToken)
    {
        request.Lookup.EnsureExactlyOne();
        var resolution = await _resolver.ResolveAsync(request.Lookup, cancellationToken);
        var recipientIds = resolution.Conversation.Participants
            .Select(p => p.Value.ToByteArray()
                .Take(8)
                .Select((b, i) => (long)b << (i * 8))
                .Aggregate((x, y) => x | y))
            .ToArray();

        await _publisher.Publish(new SignedAdminCommitOperationPostedEvent(
            resolution.Conversation.Id.Value,
            request.OpId,
            request.CommittedKeyVersion,
            request.SentUtc.UtcDateTime,
            resolution.SelfIdentityId,
            recipientIds,
            request.Signature,
            request.AdminSequenceNumber
        ), cancellationToken);
    }
}
