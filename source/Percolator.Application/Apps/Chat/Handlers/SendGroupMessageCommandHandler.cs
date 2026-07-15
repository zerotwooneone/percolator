using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Chat;
using Percolator.Chat;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Application.Network;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.App;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Contracts;
using Percolator.Identity;
using CryptoConversationId = Percolator.Cryptography.Primitives.ConversationId;

namespace Percolator.Application.Apps.Chat.Handlers;

public sealed class SendGroupMessageCommandHandler : IRequestHandler<Commands.SendGroupMessageCommand>
{
    private readonly IGroupConversationRepository _repository;
    private readonly IChatMessageWriter _messageWriter;
    private readonly IGroupMessageCryptographyService _cryptoService;
    private readonly IRemoteEnvelopeSender _envelopeSender;
    private readonly ILogger<SendGroupMessageCommandHandler> _logger;
    private readonly ISelfIdentityQueries _selfIdentityQueries;
    private readonly IDeliveryCertificateStore _certificateStore;
    private readonly ICertificateOrchestrator _certificateOrchestrator;
    private readonly TimeProvider _timeProvider;

    public SendGroupMessageCommandHandler(
        IGroupConversationRepository repository,
        IChatMessageWriter messageWriter,
        IGroupMessageCryptographyService cryptoService,
        IRemoteEnvelopeSender envelopeSender,
        ILogger<SendGroupMessageCommandHandler> logger,
        ISelfIdentityQueries selfIdentityQueries,
        IDeliveryCertificateStore certificateStore,
        ICertificateOrchestrator certificateOrchestrator,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _messageWriter = messageWriter;
        _cryptoService = cryptoService;
        _envelopeSender = envelopeSender;
        _logger = logger;
        _selfIdentityQueries = selfIdentityQueries;
        _certificateStore = certificateStore;
        _certificateOrchestrator = certificateOrchestrator;
        _timeProvider = timeProvider;
    }

    public async Task Handle(Commands.SendGroupMessageCommand request, CancellationToken cancellationToken)
    {
        var group = await _repository.GetByIdAsync(request.ConversationId, request.SelfIdentityId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            throw new InvalidOperationException($"Group conversation {request.ConversationId.Value} not found.");
        }

        var selfCryptoInfo = await _selfIdentityQueries.GetSelfIdentityCryptoInfoAsync(new SelfId(request.SelfIdentityId.Value), cancellationToken).ConfigureAwait(false);
        if (selfCryptoInfo is null)
        {
            throw new InvalidOperationException($"Self identity {request.SelfIdentityId.Value} not found.");
        }

        // Create GroupContent with text message
        var groupContent = new GroupContent
        {
            TextMessage = request.Content
        };

        // Encrypt the content using Signal SenderKey protocol
        var cryptoConversationId = new CryptoConversationId(request.ConversationId.Value);
        var cryptoPublicIdentityId = new CryptoPublicIdentity(selfCryptoInfo.Value.PublicIdentityId.Value);
        var deviceId = new Percolator.Cryptography.Primitives.DeviceId(selfCryptoInfo.Value.DeviceId.Value);
        var ciphertext = _cryptoService.EncryptGroupContent(cryptoConversationId, cryptoPublicIdentityId, deviceId, groupContent);
        
        await _messageWriter.AddTextMessageAsync(
            request.ConversationId,
            new LocalParticipantId(new Percolator.Chat.GroupLedger.PublicIdentityId(selfCryptoInfo.Value.PublicIdentityId.Value),request.SelfIdentityId),
            request.Content,
            request.PublicMessageId,
            request.SentTimestampUtc,
            cancellationToken).ConfigureAwait(false);

        // JIT: Ensure delivery certificate is available and not expired before sending to relay
        var cert = await _certificateStore.GetCertificateAsync(request.SelfIdentityId, group.RelayPeerId, cancellationToken).ConfigureAwait(false);
        if (cert == null || cert.ExpiresAtUtc < _timeProvider.GetUtcNow() + TimeSpan.FromHours(4))
        {
            _logger.LogInformation("Refreshing delivery certificate for SelfId {SelfId}, RelayPeerId {RelayPeerId} before sending group message", request.SelfIdentityId, group.RelayPeerId);
            await _certificateOrchestrator.RefreshLocalCertificateAsync(request.SelfIdentityId, group.RelayPeerId, cancellationToken).ConfigureAwait(false);
        }

        // Send to relay (Signal Group V2: sender sends once to relay, relay fans out to members)
        var identityPeerId = new Percolator.Identity.PeerId(group.RelayPeerId.Value);
        var envelope = CreateGroupMessageEnvelope(request.ConversationId, ciphertext);
        await _envelopeSender.SendChatEnvelopeToPeerAsync(envelope, identityPeerId, cancellationToken).ConfigureAwait(false);
    }

    private static ChatEnvelope CreateGroupMessageEnvelope(Percolator.Chat.Messaging.ValueObjects.ConversationId conversationId, Ciphertext ciphertext)
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
