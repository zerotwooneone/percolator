using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Chat;
using Percolator.Chat;
using Percolator.Cryptography;
using Percolator.Application.Network;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.App;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Contracts;
using Percolator.Identity;

namespace Percolator.Application.Apps.Chat.Handlers;

public sealed class SendGroupMessageCommandHandler : IRequestHandler<Commands.SendGroupMessageCommand>
{
    private readonly IGroupConversationRepository _repository;
    private readonly IChatMessageWriter _messageWriter;
    private readonly IGroupMessageCryptographyService _cryptoService;
    private readonly IGroupCryptographyService _groupCryptoService;
    private readonly IGroupCryptoStateRepository _cryptoStateRepository;
    private readonly IRemoteEnvelopeSender _envelopeSender;
    private readonly IPeerIdentityQueries _peerIdentityQueries;
    private readonly IPublisher _publisher;
    private readonly ILogger<SendGroupMessageCommandHandler> _logger;

    public SendGroupMessageCommandHandler(
        IGroupConversationRepository repository,
        IChatMessageWriter messageWriter,
        IGroupMessageCryptographyService cryptoService,
        IGroupCryptographyService groupCryptoService,
        IGroupCryptoStateRepository cryptoStateRepository,
        IRemoteEnvelopeSender envelopeSender,
        IPeerIdentityQueries peerIdentityQueries,
        IPublisher publisher,
        ILogger<SendGroupMessageCommandHandler> logger)
    {
        _repository = repository;
        _messageWriter = messageWriter;
        _cryptoService = cryptoService;
        _groupCryptoService = groupCryptoService;
        _cryptoStateRepository = cryptoStateRepository;
        _envelopeSender = envelopeSender;
        _peerIdentityQueries = peerIdentityQueries;
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

        // Load GroupMasterKey for cryptography
        var masterKey = await _cryptoStateRepository.GetGroupMasterKeyAsync(request.ConversationId, cancellationToken).ConfigureAwait(false);
        if (masterKey is null)
        {
            throw new InvalidOperationException($"Group master key not found for conversation {request.ConversationId.Value}.");
        }
        var blobKey = _groupCryptoService.DeriveBlobKey(GroupMasterKey.FromSpan(masterKey.Span));

        // Create GroupContent with text message
        var groupContent = new GroupContent
        {
            TextMessage = request.Content
        };

        // Encrypt the content
        var ciphertext = _cryptoService.EncryptGroupContent(blobKey, groupContent);

        // Write the message to local database via IChatMessageWriter
        var selfMember = group.Members.FirstOrDefault(m => m.RemovedAtUtc == null);
        if (selfMember is null)
        {
            throw new InvalidOperationException($"Self is not an active member of group {request.ConversationId.Value}.");
        }
        var senderId = selfMember.PeerId;

        await _messageWriter.AddTextMessageAsync(
            request.ConversationId,
            request.SelfIdentityId,
            senderId,
            request.Content,
            request.MessageId,
            request.SentTimestampUtc,
            cancellationToken).ConfigureAwait(false);

        // Send to relay (Signal Group V2: sender sends once to relay, relay fans out to members)
        var relayRoute = new RecipientRoute(new Percolator.Cryptography.Primitives.PeerId(group.RelayPeerId.Value), null);
        var envelope = CreateGroupMessageEnvelope(request.ConversationId, ciphertext);
        await _envelopeSender.SendChatEnvelopeToPeerAsync(envelope, relayRoute, cancellationToken).ConfigureAwait(false);
    }

    private static ChatEnvelope CreateGroupMessageEnvelope(ConversationId conversationId, Ciphertext ciphertext)
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
