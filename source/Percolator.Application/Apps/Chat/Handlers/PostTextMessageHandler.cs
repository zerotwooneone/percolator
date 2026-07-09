using MediatR;
using Percolator.Application.Apps.Chat.Commands;
using Percolator.Application.Chat;
using Percolator.Application.Identity;
using Percolator.Chat;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.App;
using Percolator.Chat.Messaging.App.Commands;
using Percolator.Chat.Messaging.Events;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Identity;
using ChatPeerId = Percolator.Chat.GroupMembership.ChatPeerId;
using PublicIdentityId = Percolator.Chat.GroupLedger.PublicIdentityId;

namespace Percolator.Application.Apps.Chat.Handlers;

public sealed class PostTextMessageHandler : IRequestHandler<PostTextMessageCommand>
{
    private readonly IDirectConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;
    private readonly IMediator _mediator;
    private readonly ISelfIdentityQueries _selfIdentityQueries;

    public PostTextMessageHandler(
        IDirectConversationResolver resolver,
        IChatMessageWriter writer,
        IMediator mediator,
        ISelfIdentityQueries selfIdentityQueries)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _selfIdentityQueries = selfIdentityQueries;
    }

    public async Task Handle(PostTextMessageCommand request, CancellationToken cancellationToken)
    {
        request.LookupKey.EnsureExactlyOne();

        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken).ConfigureAwait(false);
        
        var selfPublicIdentityId = await _selfIdentityQueries.GetSelfIdentityPublicKeyAsync(new SelfId(request.SelfIdentityId.Value), cancellationToken).ConfigureAwait(false);
        if (selfPublicIdentityId is null)
        {
            throw new InvalidOperationException($"Self identity {request.SelfIdentityId.Value} not found.");
        }
        
        await _writer.AddTextMessageAsync(
            resolution.Conversation.Id,
            new LocalParticipantId(new PublicIdentityId(selfPublicIdentityId.Value), request.SelfIdentityId),
            request.Content,
            request.PublicMessageId,
            request.SentTimestampUtc,
            cancellationToken).ConfigureAwait(false);

        var peerId = new Percolator.Identity.PeerId( resolution.Conversation.Peer1.Value);
        await _mediator.Send(new DispatchTextMessageCommand(
            request.PublicMessageId.Value,
            request.Content,
            request.SentTimestampUtc,
            new[] {peerId}), cancellationToken).ConfigureAwait(false);
    }
}
