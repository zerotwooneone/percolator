using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Chat;
using Percolator.Chat.App;
using Percolator.Chat.Events;
using Percolator.Cryptography;

namespace Percolator.Application.Apps.Chat.Handlers;

public sealed class SendGroupMessageCommandHandler : IRequestHandler<Commands.SendGroupMessageCommand>
{
    private readonly IGroupConversationRepository _repository;
    private readonly IChatMessageWriter _messageWriter;
    private readonly IConversationMemberQueries _memberQueries;
    private readonly IGroupMessageCryptographyService _cryptoService;
    private readonly IPublisher _publisher;
    private readonly ILogger<SendGroupMessageCommandHandler> _logger;

    public SendGroupMessageCommandHandler(
        IGroupConversationRepository repository,
        IChatMessageWriter messageWriter,
        IConversationMemberQueries memberQueries,
        IGroupMessageCryptographyService cryptoService,
        IPublisher publisher,
        ILogger<SendGroupMessageCommandHandler> logger)
    {
        _repository = repository;
        _messageWriter = messageWriter;
        _memberQueries = memberQueries;
        _cryptoService = cryptoService;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task Handle(Commands.SendGroupMessageCommand request, CancellationToken cancellationToken)
    {
        var group = await _repository.GetByIdAsync(request.ConversationId, request.SelfIdentityId, cancellationToken);
        if (group is null)
        {
            throw new InvalidOperationException($"Group conversation {request.ConversationId.Value} not found.");
        }

        // Fetch members with route info to dispatch messages
        var members = await _memberQueries.GetGroupMembersWithRoutesAsync(request.ConversationId, request.SelfIdentityId, cancellationToken);
        var activeMembers = members.Where(m => m.RemovedAtUtc == null).ToList();

        // Write the message to local database via IChatMessageWriter
        var selfParticipantId = members.FirstOrDefault(m => m.PeerId.Value == group.State.ConversationId.Value)?.PeerId.Value ?? Guid.Empty; // Temporary placeholder for sender logic

        await _messageWriter.AddMessageAsync(
            request.ConversationId,
            request.SelfIdentityId,
            selfParticipantId,
            request.MessageId,
            request.Content,
            request.SentTimestampUtc,
            cancellationToken);

        // TODO: Cryptography & Fanout logic

        // Publish event for UI update
        var recipientPeerIds = activeMembers.Select(m => m.PeerId.Value).ToList();
        await _publisher.Publish(new TextMessagePostedEvent(
            request.ConversationId.Value,
            request.MessageId.Value,
            request.SelfIdentityId,
            recipientPeerIds,
            request.Content,
            request.SentTimestampUtc,
            null), // Group conversations do not have a DirectSessionId
            cancellationToken);
    }
}
