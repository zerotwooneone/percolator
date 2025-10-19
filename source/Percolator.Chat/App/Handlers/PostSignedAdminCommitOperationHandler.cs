using MediatR;
using Percolator.Chat.App;
using Percolator.Chat.App.Commands;
using Percolator.Chat.Events;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App.Handlers;

public sealed class PostSignedAdminCommitOperationHandler : IRequestHandler<PostSignedAdminCommitOperationCommand>
{
    private readonly IConversationResolver _resolver;
    private readonly IPublisher _publisher;
    private readonly ISelfIdentityProvider _selfIdentityProvider;

    public PostSignedAdminCommitOperationHandler(
        IConversationResolver resolver,
        IPublisher publisher,
        ISelfIdentityProvider selfIdentityProvider)
    {
        _resolver = resolver;
        _publisher = publisher;
        _selfIdentityProvider = selfIdentityProvider;
    }

    public async Task Handle(PostSignedAdminCommitOperationCommand request, CancellationToken cancellationToken)
    {
        request.Lookup.EnsureExactlyOne();
        var resolution = await _resolver.ResolveAsync(request.Lookup, cancellationToken);

        // Compute recipients (exclude self)
        var peerId = await _selfIdentityProvider.GetPeerIdAsync(resolution.SelfIdentityId, cancellationToken);
        var selfParticipantId = new ParticipantId(peerId);

        var recipientIds = resolution.Conversation.Participants
            .Where(p => p != selfParticipantId)
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
