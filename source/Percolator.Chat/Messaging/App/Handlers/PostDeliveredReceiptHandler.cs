using MediatR;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.App.Commands;
using Percolator.Chat.Messaging.Events;

namespace Percolator.Chat.Messaging.App.Handlers;

public sealed class PostDeliveredReceiptHandler : IRequestHandler<PostDeliveredReceiptCommand>
{
    private readonly IDirectConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;
    private readonly IPublisher _publisher;
    private readonly ISelfParticipantIdProvider _selfParticipantIdProvider;

    public PostDeliveredReceiptHandler(IDirectConversationResolver resolver, IChatMessageWriter writer, IPublisher publisher, ISelfParticipantIdProvider selfParticipantIdProvider)
    {
        _resolver = resolver;
        _writer = writer;
        _publisher = publisher;
        _selfParticipantIdProvider = selfParticipantIdProvider;
    }

    public async Task Handle(PostDeliveredReceiptCommand request, CancellationToken cancellationToken)
    {
        request.LookupKey.EnsureExactlyOne();
        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken);
        var selfParticipantId = _selfParticipantIdProvider.Get();

        await _writer.AddDeliveredReceiptAsync(
            resolution.Conversation.Id,
            resolution.SelfIdentityId,
            selfParticipantId,
            request.MessageId,
            request.DeliveredAtUtc,
            cancellationToken);

        await _publisher.Publish(new DeliveredReceiptPostedEvent(
            resolution.Conversation.Id.Value,
            request.MessageId.Value,
            new ChatPeerId(resolution.SelfIdentityId),
            new[] { new ChatPeerId(resolution.Conversation.Peer1.Value), new ChatPeerId(resolution.Conversation.Peer2.Value) },
            request.DeliveredAtUtc.UtcDateTime),
            cancellationToken);
    }
}
