using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Apps.Chat.Queries;
using Percolator.Chat;
using Percolator.Chat.App;
using Percolator.Chat.Events;
using Percolator.Cryptography;
using Percolator.Application.Network;
using Percolator.Chat.ValueObjects;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using Percolator.Contracts;

namespace Percolator.Application.Apps.Chat.Handlers;

public sealed class SendGroupMessageCommandHandler : IRequestHandler<Commands.SendGroupMessageCommand>
{
    private readonly IGroupConversationRepository _repository;
    private readonly IChatMessageWriter _messageWriter;
    private readonly IConversationMemberQueries _memberQueries;
    private readonly IGroupMessageCryptographyService _cryptoService;
    private readonly IGroupCryptographyService _groupCryptoService;
    private readonly IGroupCryptoStateRepository _cryptoStateRepository;
    private readonly IRemoteEnvelopeSender _envelopeSender;
    private readonly IPublisher _publisher;
    private readonly ILogger<SendGroupMessageCommandHandler> _logger;

    public SendGroupMessageCommandHandler(
        IGroupConversationRepository repository,
        IChatMessageWriter messageWriter,
        IConversationMemberQueries memberQueries,
        IGroupMessageCryptographyService cryptoService,
        IGroupCryptographyService groupCryptoService,
        IGroupCryptoStateRepository cryptoStateRepository,
        IRemoteEnvelopeSender envelopeSender,
        IPublisher publisher,
        ILogger<SendGroupMessageCommandHandler> logger)
    {
        _repository = repository;
        _messageWriter = messageWriter;
        _memberQueries = memberQueries;
        _cryptoService = cryptoService;
        _groupCryptoService = groupCryptoService;
        _cryptoStateRepository = cryptoStateRepository;
        _envelopeSender = envelopeSender;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task Handle(Commands.SendGroupMessageCommand request, CancellationToken cancellationToken)
    {
        var group = await _repository.GetByIdAsync(request.ConversationId, request.SelfIdentityId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            throw new InvalidOperationException($"Group conversation {request.ConversationId.Value} not found.");
        }

        // Fetch members with route info to dispatch messages
        var members = await _memberQueries.GetGroupMembersWithRoutesAsync(request.ConversationId, request.SelfIdentityId, cancellationToken).ConfigureAwait(false);
        var activeMembers = members.Where(m => m.RemovedAtUtc == null).ToList();

        if (activeMembers.Count == 0)
        {
            throw new InvalidOperationException($"Group conversation {request.ConversationId.Value} has no active members.");
        }

        // Load GroupMasterKey for cryptography
        var masterKey = await _cryptoStateRepository.GetGroupMasterKeyAsync(request.ConversationId, cancellationToken).ConfigureAwait(false);
        if (masterKey is null)
        {
            throw new InvalidOperationException($"Group master key not found for conversation {request.ConversationId.Value}.");
        }
        var blobKey = _groupCryptoService.DeriveBlobKey(masterKey);

        // Create GroupContent with text message
        var groupContent = new GroupContent
        {
            TextMessage = request.Content
        };

        // Encrypt the content
        var ciphertext = _cryptoService.EncryptGroupContent(blobKey, groupContent);

        // Write the message to local database via IChatMessageWriter
        var selfParticipantId = members.FirstOrDefault(m => m.PeerId.Value == group.State.ConversationId.Value)?.PeerId.Value ?? Guid.Empty;
        var senderId = new ParticipantId(selfParticipantId);

        await _messageWriter.AddTextMessageAsync(
            request.ConversationId,
            request.SelfIdentityId,
            senderId,
            request.Content,
            request.MessageId,
            request.SentTimestampUtc,
            cancellationToken).ConfigureAwait(false);

        // Hybrid delivery: group members by delivery path (direct vs relay)
        var directRecipients = new List<RecipientRoute>();
        var relayRecipients = new Dictionary<RecipientRoute, List<Percolator.Identity.PeerId>>();

        foreach (var member in activeMembers)
        {
            if (member.DeliveryRoute is null)
            {
                _logger.LogWarning("No delivery route available for peer {PeerId}, skipping", member.PeerId.Value);
                continue;
            }

            // Check if this is a relay delivery (route peer differs from member peer)
            if (member.DeliveryRoute.PeerId.Value != member.PeerId.Value)
            {
                // Relay delivery
                if (!relayRecipients.ContainsKey(member.DeliveryRoute))
                {
                    relayRecipients[member.DeliveryRoute] = new List<Percolator.Identity.PeerId>();
                }
                relayRecipients[member.DeliveryRoute].Add(member.PeerId);
            }
            else
            {
                // Direct delivery
                directRecipients.Add(member.DeliveryRoute);
            }
        }

        // Send to direct peers
        foreach (var route in directRecipients)
        {
            var envelope = CreateGroupMessageEnvelope(request.ConversationId, ciphertext);
            await _envelopeSender.SendChatEnvelopeToPeerAsync(envelope, route, cancellationToken).ConfigureAwait(false);
        }

        // Send to relay peers (relay fans out to its members)
        foreach (var (relayRoute, memberPeerIds) in relayRecipients)
        {
            var envelope = CreateGroupMessageEnvelope(request.ConversationId, ciphertext);
            await _envelopeSender.SendChatEnvelopeToPeerAsync(envelope, relayRoute, cancellationToken).ConfigureAwait(false);
        }

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
            cancellationToken).ConfigureAwait(false);
    }

    private static ChatEnvelope CreateGroupMessageEnvelope(ChatConversationId conversationId, Ciphertext ciphertext)
    {
        return new ChatEnvelope
        {
            GroupMessage = new GroupMessage
            {
                ConversationId = Google.Protobuf.ByteString.CopyFrom(conversationId.Value.ToByteArray()),
                Ciphertext = Google.Protobuf.ByteString.CopyFrom(ciphertext.Span.ToArray())
            }
        };
    }
}
