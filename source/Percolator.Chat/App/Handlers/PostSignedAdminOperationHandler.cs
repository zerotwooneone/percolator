using MediatR;
using Percolator.Chat.App;
using Percolator.Chat.App.Commands;
using Percolator.Chat.Events;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App.Handlers;

public sealed class PostSignedAdminOperationHandler : IRequestHandler<PostSignedAdminOperationCommand>
{
    private readonly IConversationResolver _resolver;
    private readonly IPublisher _publisher;
    private readonly ISelfIdentityProvider _selfIdentityProvider;

    public PostSignedAdminOperationHandler(
        IConversationResolver resolver,
        IPublisher publisher,
        ISelfIdentityProvider selfIdentityProvider)
    {
        _resolver = resolver;
        _publisher = publisher;
        _selfIdentityProvider = selfIdentityProvider;
    }

    public async Task Handle(PostSignedAdminOperationCommand request, CancellationToken cancellationToken)
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

        await _publisher.Publish(new SignedAdminOperationPostedEvent(
            resolution.Conversation.Id.Value,
            request.OpId,
            request.SentUtc.UtcDateTime,
            resolution.SelfIdentityId,
            recipientIds,
            request.Kind,
            request.GranteePublicKeySpki,
            request.MembersToAdd,
            request.MembersToRemove,
            request.LeaveGroup,
            request.NewGroupName,
            request.NewGroupAvatar,
            request.Signature,
            request.AdminSequenceNumber
        ), cancellationToken);
    }
}
