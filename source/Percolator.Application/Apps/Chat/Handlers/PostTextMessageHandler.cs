using MediatR;
using Percolator.Application.Apps.Chat.Commands;
using Percolator.Application.Identity;
using Percolator.Chat;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.App;
using Percolator.Chat.Messaging.App.Commands;
using Percolator.Chat.Messaging.Events;
using Percolator.Chat.Messaging.ValueObjects;
using ChatPeerId = Percolator.Chat.GroupMembership.ChatPeerId;

namespace Percolator.Application.Apps.Chat.Handlers;

public sealed class PostTextMessageHandler : IRequestHandler<PostTextMessageCommand>
{
    private readonly IDirectConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;
    private readonly IPublisher _publisher;
    private readonly ActiveIdentityContext _active;

    public PostTextMessageHandler(
        IDirectConversationResolver resolver,
        IChatMessageWriter writer,
        IPublisher publisher,
        ActiveIdentityContext active)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _active = active ?? throw new ArgumentNullException(nameof(active));
    }

    public async Task Handle(PostTextMessageCommand request, CancellationToken cancellationToken)
    {
        request.LookupKey.EnsureExactlyOne();

        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken).ConfigureAwait(false);
        
        await _writer.AddTextMessageAsync(
            resolution.Conversation.Id,
            resolution.SelfIdentityId,
            resolution.Conversation.Peer1,
            request.Content,
            request.MessageId,
            request.SentTimestampUtc,
            cancellationToken).ConfigureAwait(false);

        var peerId = new Percolator.Identity.PeerId( resolution.Conversation.Peer1.Value);
        await _publisher.Publish(new DispatchTextMessageCommand(
            request.MessageId.Value,
            request.Content,
            request.SentTimestampUtc,
            new[] {peerId}), cancellationToken).ConfigureAwait(false);
    }
}
