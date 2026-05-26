using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Apps.Chat;
using Percolator.Chat.App;
using Percolator.Chat.App.Commands;
using Percolator.Chat.Primitives;
using Percolator.Chat.ValueObjects;
using Percolator.Contracts;
using Percolator.Dht;
using Percolator.Application.Network.Handshake;
using Percolator.Identity;
using Percolator.MessageQueue.Commands;
using Percolator.MessageQueue.Abstractions;
using Percolator.Network;
using Percolator.Prekey.Handlers;
using System.Text.Json;
using Percolator.Chat;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Network;

internal sealed class ProcessInternalEnvelopeHandler : IRequestHandler<ProcessInternalEnvelopeCommand, InternalEnvelope?>
{
    private readonly ILogger<ProcessInternalEnvelopeHandler> _logger;
    private readonly IMediator _mediator;
    private readonly IDhtService _dhtService;
    private readonly IPeerRoutingProfileRepository _profileRepository;
    private readonly IMessageQueueService _mqService;
    private readonly Percolator.Chat.App.IPkhPeerResolver _pkhPeerResolver;
    private readonly IPendingGroupInvitationRepository _pendingGroupInvitationRepository;
    private readonly Percolator.Chat.App.IGroupCryptoStateRepository _groupCryptoStateRepository;

    public ProcessInternalEnvelopeHandler(ILogger<ProcessInternalEnvelopeHandler> logger, IMediator mediator, IDhtService dhtService, IMessageQueueService mqService, IPeerRoutingProfileRepository profileRepository, Percolator.Chat.App.IPkhPeerResolver pkhPeerResolver, IPendingGroupInvitationRepository pendingGroupInvitationRepository, Percolator.Chat.App.IGroupCryptoStateRepository groupCryptoStateRepository)
    {
        _logger = logger;
        _mediator = mediator;
        _dhtService = dhtService;
        _mqService = mqService;
        _profileRepository = profileRepository;
        _pkhPeerResolver = pkhPeerResolver;
        _pendingGroupInvitationRepository = pendingGroupInvitationRepository;
        _groupCryptoStateRepository = groupCryptoStateRepository;
    }

    public async Task<InternalEnvelope?> Handle(ProcessInternalEnvelopeCommand request, CancellationToken cancellationToken)
    {
        var env = request.Envelope;
        _logger.LogDebug("Processing InternalEnvelope with case {Case}", env.ApplicationPayloadCase);

        // DHT handling
        if (env.ApplicationPayloadCase == InternalEnvelope.ApplicationPayloadOneofCase.DhtEnvelope)
        {
            var dht = env.DhtEnvelope;
            if (dht.FindNodeRequest is not null)
            {
                if (!dht.FindNodeRequest.HasTargetPeerId || dht.FindNodeRequest.TargetPeerId.Length == 0)
                {
                    _logger.LogWarning("FindNodeRequest missing target_peer_id");
                    return null;
                }

                var closerNodes = await _dhtService.GetClosestNodesAsync(NodeId.FromSpan(dht.FindNodeRequest.TargetPeerId.Span), cancellationToken).ConfigureAwait(false);

                var outResp = new Contracts.FindNodeResponse();
                foreach (var node in closerNodes)
                {
                    outResp.CloserPeers.Add(new Contracts.NodeInfo
                    {
                        PeerId = ByteString.CopyFrom(node.Id.Span),
                        Address = $"{node.EndPoint.Host}:{node.EndPoint.Port}"
                    });
                }
                return new InternalEnvelope { DhtEnvelope = new Contracts.DhtEnvelope { FindNodeResponse = outResp } };
            }
            else if (dht.PingRequest is not null)
            {
                // Compute NodeId from remote peer's signing key
                if (request.Context.RemotePeerGuid is null)
                {
                    _logger.LogWarning("PingRequest received without RemotePeerGuid in context");
                    return null;
                }
                var remotePeerId = new Percolator.Network.PeerId(request.Context.RemotePeerGuid.Value);
                var profile = await _profileRepository.GetByIdAsync(remotePeerId, cancellationToken).ConfigureAwait(false);
                if (profile is null || profile.IdentityPublicKey is null)
                {
                    _logger.LogWarning("No routing profile or signing key for remote peer {PeerId} to handle PingRequest", remotePeerId);
                    return null;
                }
                var freshest = profile.Endpoints
                    .OrderByDescending(e => e.LastSeen)
                    .FirstOrDefault();
                if (freshest.EndPoint == null)
                {
                    _logger.LogWarning("No endpoints in routing profile for remote peer {PeerId} to handle PingRequest", remotePeerId);
                    return null;
                }
                var nodeIdBytes = System.Security.Cryptography.SHA256.HashData(profile.IdentityPublicKey.Span);
                await _mediator.Send(new Percolator.Dht.Messages.PingRequest(NodeId.FromBytesOwned(nodeIdBytes), freshest.EndPoint), cancellationToken).ConfigureAwait(false);
                return null;
            }
            return null;
        }

        // Prekey handling
        if (env.ApplicationPayloadCase == InternalEnvelope.ApplicationPayloadOneofCase.PrekeyEnvelope)
        {
            var pre = env.PrekeyEnvelope;
            switch (pre.MessageCase)
            {
                case PrekeyEnvelope.MessageOneofCase.SubmitPreKeyBundleRequest:
                {
                    var upload = pre.SubmitPreKeyBundleRequest;
                    if (!upload.HasIdentityKey) throw new InvalidOperationException("Identity key is required");
                    if (!upload.HasSignedPreKeyId) throw new InvalidOperationException("Signed pre-key ID is required");
                    if (!upload.HasSignedPreKey) throw new InvalidOperationException("Signed pre-key is required");
                    if (!upload.HasPreKeySignature) throw new InvalidOperationException("Pre-key signature is required");
                    if (upload.OneTimePreKeys.Count == 0) throw new InvalidOperationException("At least one one-time pre-key is required");
                    if(request.Context.RemotePeerGuid is null) throw new InvalidOperationException($"{nameof(request)} must have a {nameof(ProcessInternalEnvelopeCommand.Context.RemotePeerGuid)}");
                    const int maxBundles = 100;
                    if (upload.OneTimePreKeys.Count > maxBundles) throw new InvalidOperationException($"Too many one-time pre-keys. Maximum is {maxBundles}");
                    foreach (var ot in upload.OneTimePreKeys)
                    {
                        if (!ot.HasId) throw new InvalidOperationException("One-time pre-key is required");
                        if (!ot.HasPublicKey) throw new InvalidOperationException("One-time pre-key public key is required");
                    }
                    if (upload.ExpiresUtc.ToDateTimeOffset() < DateTimeOffset.Now) throw new InvalidOperationException("Pre-key bundle has expired");

                    var cmd = new SubmitPreKeyBundleCommand
                    {
                        PublicSigningKey = upload.IdentityKey.ToByteArray(),
                        SignedPreKeyId = new Guid(upload.SignedPreKeyId.Span),
                        SignedPreKey = upload.SignedPreKey.ToByteArray(),
                        PreKeySignature = upload.PreKeySignature.ToByteArray(),
                        OneTimePreKeys = upload.OneTimePreKeys.Select(x => new SubmitPreKeyBundleCommand.OneTimePreKey(new Guid(x.Id.Span), x.PublicKey.ToByteArray())).ToList(),
                        Expires = upload.ExpiresUtc.ToDateTimeOffset(),
                        RemotePeerId = new Percolator.Network.PeerId(request.Context.RemotePeerGuid.Value)
                    };
                    await _mediator.Send(cmd, cancellationToken).ConfigureAwait(false);
                    return new InternalEnvelope { SubmitPreKeyBundleResponse = new SubmitPreKeyBundleResponse { Version = 1 } };
                }
                case PrekeyEnvelope.MessageOneofCase.GetPreKeyBundleRequest:
                {
                    var getReq = pre.GetPreKeyBundleRequest;
                    if (!getReq.HasPublicKeyHash) throw new InvalidOperationException("PublicKeyHash is required");
                    var bundle = await _mediator.Send(new GetPreKeyBundleQuery(
                        IdentityPublicKeyHash.FromSpan(getReq.PublicKeyHash.Span)), cancellationToken).ConfigureAwait(false);
                    var resp = new GetPreKeyBundleResponse { Version = 1 };
                    if (bundle is not null)
                    {
                        var msg = new GetPreKeyBundleResponse.Types.PreKeyBundle
                        {
                            Version = 1,
                            IdentityKey = Google.Protobuf.ByteString.CopyFrom(bundle.IdentitySigningKey.Span),
                            SignedPreKeyId = Google.Protobuf.ByteString.CopyFrom(bundle.SignedPreKeyId.ToByteArray()),
                            SignedPreKey = Google.Protobuf.ByteString.CopyFrom(bundle.SignedPreKey.Span),
                            PreKeySignature = Google.Protobuf.ByteString.CopyFrom(bundle.SignedPreKeySignature.Span)
                        };
                        if (bundle.OneTimePreKey is not null)
                        {
                            msg.OneTimeKeys.Add(new GetPreKeyBundleResponse.Types.OneTimeKey
                            {
                                Version = 1,
                                OneTimeKeyId = Google.Protobuf.ByteString.CopyFrom(bundle.OneTimePreKeyId!.Value.ToByteArray()),
                                KeyBytes = Google.Protobuf.ByteString.CopyFrom(bundle.OneTimePreKey.Span)
                            });
                        }
                        resp.PreKeyBundle = msg;
                    }
                    return new InternalEnvelope { GetPreKeyBundleResponse = resp };
                }
                default:
                    _logger.LogWarning("Unhandled PrekeyEnvelope type: {Type}", pre.MessageCase);
                    return null;
            }
        }

        // RelayOpaque handling (processing only; RPC-level ack is handled in DeliverOpaqueMessageHandler)
        if (env.ApplicationPayloadCase == InternalEnvelope.ApplicationPayloadOneofCase.RelayOpaqueEnvelope)
        {
            if(request.Context.RemotePeerGuid is null) throw new InvalidOperationException($"{nameof(request)} must have a {nameof(ProcessInternalEnvelopeCommand.Context.RemotePeerGuid)}");
            var relayPeerId = new Percolator.Identity.PeerId(request.Context.RemotePeerGuid.Value);
            var relay = env.RelayOpaqueEnvelope;
            await _mediator.Send(new ProcessRelayedOpaquePayloadCommand(request.Context.SelfIdentityId, Payload.FromBytesOwned(relay.OpaquePayload.ToByteArray()), relayPeerId)).ConfigureAwait(false);
            return null;
        }

        // Chat handling
        if (env.ApplicationPayloadCase == InternalEnvelope.ApplicationPayloadOneofCase.ChatEnvelope)
        {
            var chat = env.ChatEnvelope;
            switch (chat.MessageCase)
            {
                case ChatEnvelope.MessageOneofCase.TextMessage:
                {
                    var text = chat.TextMessage;
                    if (text.MessageId == null || text.MessageId.Length != 16)
                        throw new InvalidOperationException("TextMessage.message_id must be 16 bytes (GUID).");

                    byte[]? pkh = null;
                    if (text.HasPublicKeyHash)
                    {
                        if (text.PublicKeyHash.Length != 32)
                            throw new InvalidOperationException("TextMessage.public_key_hash must be 32 bytes (SHA-256).");
                        pkh = text.PublicKeyHash.ToByteArray();
                    }
                    ConversationLookupKey lookup = (pkh is not null && pkh.Length > 0)
                        ? ConversationLookupKey.ForPublicKeyHash(Pkh.FromBytes(pkh))
                        : ConversationLookupKey.ForDirectSession(request.Context.SessionId ?? throw new InvalidOperationException("SessionId required when no routing hint provided."));

                    // Direct chat: Use RemotePeerGuid
                    if (request.Context.RemotePeerGuid is null)
                        throw new InvalidOperationException("Direct chat TextMessage requires RemotePeerGuid in context.");
                    var senderId = new ParticipantId(request.Context.RemotePeerGuid.Value);

                    var messageId = new MessageId(new Guid(text.MessageId.Span));
                    var sentTs = text.SentTimestampUtc.ToDateTimeOffset();
                    await _mediator.Send(new ReceiveTextMessageCommand(lookup, senderId, messageId, text.Content, sentTs), cancellationToken).ConfigureAwait(false);
                    return null;
                }
                case ChatEnvelope.MessageOneofCase.ReadReceipt:
                {
                    var rr = chat.ReadReceipt;
                    if (rr.MessageId == null || rr.MessageId.Length != 16)
                        throw new InvalidOperationException("ReadReceipt.message_id must be 16 bytes (GUID).");

                    byte[]? pkh = null;
                    if (rr.HasPublicKeyHash)
                    {
                        if (rr.PublicKeyHash.Length != 32)
                            throw new InvalidOperationException("ReadReceipt.public_key_hash must be 32 bytes (SHA-256).");
                        pkh = rr.PublicKeyHash.ToByteArray();
                    }
                    var lookup = (pkh is not null && pkh.Length > 0)
                        ? ConversationLookupKey.ForPublicKeyHash(Pkh.FromBytes(pkh))
                        : ConversationLookupKey.ForDirectSession(request.Context.SessionId ?? throw new InvalidOperationException("SessionId required when no routing hint provided."));

                    // Direct chat: Use RemotePeerGuid
                    if (request.Context.RemotePeerGuid is null)
                        throw new InvalidOperationException("Direct chat ReadReceipt requires RemotePeerGuid in context.");
                    var readerId = new ParticipantId(request.Context.RemotePeerGuid.Value);

                    var messageId = new MessageId(new Guid(rr.MessageId.Span));
                    var ts = rr.SentTimestampUtc.ToDateTimeOffset();
                    await _mediator.Send(new ReceiveReadReceiptCommand(lookup, readerId, messageId, ts), cancellationToken).ConfigureAwait(false);
                    return null;
                }
                case ChatEnvelope.MessageOneofCase.EmojiAnnotation:
                {
                    var em = chat.EmojiAnnotation;
                    if (em.MessageId == null || em.MessageId.Length != 16)
                        throw new InvalidOperationException("EmojiAnnotation.message_id must be 16 bytes (GUID).");
                    if (string.IsNullOrWhiteSpace(em.Emoji))
                        throw new InvalidOperationException("EmojiAnnotation.emoji is required.");

                    byte[]? pkh = null;
                    if (em.HasPublicKeyHash)
                    {
                        if (em.PublicKeyHash.Length != 32)
                            throw new InvalidOperationException("EmojiAnnotation.public_key_hash must be 32 bytes (SHA-256).");
                        pkh = em.PublicKeyHash.ToByteArray();
                    }
                    var lookup = (pkh is not null && pkh.Length > 0)
                        ? ConversationLookupKey.ForPublicKeyHash(Pkh.FromBytes(pkh))
                        : ConversationLookupKey.ForDirectSession(request.Context.SessionId ?? throw new InvalidOperationException("SessionId required when no routing hint provided."));

                    // Direct chat: Use RemotePeerGuid
                    if (request.Context.RemotePeerGuid is null)
                        throw new InvalidOperationException("Direct chat EmojiAnnotation requires RemotePeerGuid in context.");
                    var reactorId = new ParticipantId(request.Context.RemotePeerGuid.Value);

                    var messageId = new MessageId(new Guid(em.MessageId.Span));
                    var ts = em.SentTimestampUtc.ToDateTimeOffset();
                    await _mediator.Send(new ReceiveEmojiAnnotationCommand(lookup, reactorId, messageId, em.Emoji, ts), cancellationToken).ConfigureAwait(false);
                    return null;
                }
                case ChatEnvelope.MessageOneofCase.DeliveredReceipt:
                {
                    var dr = chat.DeliveredReceipt;
                    if (dr.MessageId == null || dr.MessageId.Length != 16)
                        throw new InvalidOperationException("DeliveredReceipt.message_id must be 16 bytes (GUID).");

                    byte[]? pkh = null;
                    if (dr.HasPublicKeyHash)
                    {
                        if (dr.PublicKeyHash.Length != 32)
                            throw new InvalidOperationException("DeliveredReceipt.public_key_hash must be 32 bytes (SHA-256).");
                        pkh = dr.PublicKeyHash.ToByteArray();
                    }
                    var lookup = (pkh is not null && pkh.Length > 0)
                        ? ConversationLookupKey.ForPublicKeyHash(Pkh.FromBytes(pkh))
                        : ConversationLookupKey.ForDirectSession(request.Context.SessionId ?? throw new InvalidOperationException("SessionId required when no routing hint provided."));

                    // Direct chat: Use RemotePeerGuid
                    if (request.Context.RemotePeerGuid is null)
                        throw new InvalidOperationException("Direct chat DeliveredReceipt requires RemotePeerGuid in context.");
                    var recipientId = new ParticipantId(request.Context.RemotePeerGuid.Value);

                    var messageId = new MessageId(new Guid(dr.MessageId.Span));
                    var ts = dr.SentTimestampUtc.ToDateTimeOffset();
                    await _mediator.Send(new ReceiveDeliveredReceiptCommand(lookup, recipientId, messageId, ts), cancellationToken).ConfigureAwait(false);
                    return null;
                }
                case ChatEnvelope.MessageOneofCase.CreateGroup:
                {
                    var chatEnv = env.ChatEnvelope;
                    var createGroup = chatEnv.CreateGroup;
                    
                    // Validate conversation_id is 16 bytes and not empty
                    if (createGroup.ConversationId.Length != 16)
                    {
                        _logger.LogWarning("Invalid CreateGroup: conversation_id must be 16 bytes, got {Length}", createGroup.ConversationId.Length);
                        return null;
                    }
                    var conversationId = new Guid(createGroup.ConversationId.ToByteArray());
                    if (conversationId == Guid.Empty)
                    {
                        _logger.LogWarning("Invalid CreateGroup: conversation_id cannot be empty");
                        return null;
                    }

                    // Validate initial_participant_identity_keys is not empty
                    if (createGroup.InitialParticipantIdentityKeys.Count == 0)
                    {
                        _logger.LogWarning("Invalid CreateGroup: initial_participant_identity_keys cannot be empty");
                        return null;
                    }

                    // Serialize initial member identity keys to JSON
                    var initialMembers = createGroup.InitialParticipantIdentityKeys
                        .Select(k => k.ToByteArray())
                        .ToList();
                    var initialMembersJson = JsonSerializer.Serialize(initialMembers);

                    if (request.Context.RemotePeerGuid == null)
                    {
                        _logger.LogWarning("Invalid CreateGroup: RemotePeerGuid missing from envelope context");
                        return null;
                    }
                    var senderPeerId = new PeerId(request.Context.RemotePeerGuid.Value);

                    // Persist pending group invitation
                    var pendingInvitation = new PendingGroupInvitation(
                        Guid.NewGuid(),
                        new ConversationId(conversationId),
                        senderPeerId,
                        createGroup.CreatorIdentityKey.ToByteArray(),
                        initialMembers,
                        createGroup.Name,
                        DateTimeOffset.UtcNow
                    );

                    await _pendingGroupInvitationRepository.AddAsync(pendingInvitation, cancellationToken);

                    _logger.LogInformation("Received CreateGroup message for conversation {ConversationId} from {SenderPeerId}, persisted as pending invitation", conversationId, senderPeerId);
                    
                    // TODO: Dispatch notification for UI to surface in connection management dialog
                    return null;
                }
                case ChatEnvelope.MessageOneofCase.GroupKeyBootstrap:
                {
                    var chatEnv = env.ChatEnvelope;
                    var bootstrap = chatEnv.GroupKeyBootstrap;
                    
                    // Validate conversation_id is exactly 16 bytes and not Guid.Empty
                    if (bootstrap.ConversationId.Length != 16)
                    {
                        _logger.LogWarning("Invalid GroupKeyBootstrap: conversation_id must be 16 bytes, got {Length}", bootstrap.ConversationId.Length);
                        return null;
                    }
                    var conversationId = new Guid(bootstrap.ConversationId.ToByteArray());
                    if (conversationId == Guid.Empty)
                    {
                        _logger.LogWarning("Invalid GroupKeyBootstrap: conversation_id cannot be empty");
                        return null;
                    }

                    // Validate group_master_key_bytes is exactly 32 bytes
                    if (bootstrap.GroupMasterKeyBytes.Length != 32)
                    {
                        _logger.LogWarning("Invalid GroupKeyBootstrap: group_master_key_bytes must be 32 bytes, got {Length}", bootstrap.GroupMasterKeyBytes.Length);
                        return null;
                    }

                    // Derive GroupId from master key via KDF (for logging/validation)
                    var groupMasterKey = Percolator.Cryptography.GroupMasterKey.FromBytes(bootstrap.GroupMasterKeyBytes.ToByteArray());
                    
                    // Persist via IGroupCryptoStateRepository
                    await _groupCryptoStateRepository.UpsertGroupMasterKeyAsync(
                        new Percolator.Chat.ValueObjects.ConversationId(conversationId),
                        groupMasterKey,
                        cancellationToken);

                    _logger.LogInformation("Received GroupKeyBootstrap message for conversation {ConversationId}, persisted GroupMasterKey", conversationId);
                    return null;
                }
                case ChatEnvelope.MessageOneofCase.GroupMessage:
                {
                    // Skeleton: Business logic to be implemented in Chunk D
                    _logger.LogInformation("Received GroupMessage message (skeleton handler)");
                    return null;
                }
                default:
                    return null;
            }
        }

        // Handshake responder hello removed: responder message is a DR plaintext ResponderInnerHello, handled outside orchestrator.

        // MQ handling
        if (env.ApplicationPayloadCase == InternalEnvelope.ApplicationPayloadOneofCase.MessageQueueEnvelope)
        {
            var mq = env.MessageQueueEnvelope;
            switch (mq.MessageCase)
            {
                case MessageQueueEnvelope.MessageOneofCase.EnqueueOpaqueMessageRequest:
                {
                    var req = mq.EnqueueOpaqueMessageRequest;
                    var enqueueResult = await _mqService.EnqueueOpaqueAsync(
                        req.RecipientPublicKeyHash.ToByteArray(),
                        req.MessageBlob.ToByteArray(),
                        cancellationToken).ConfigureAwait(false);

                    var resp = new EnqueueOpaqueMessageResponse
                    {
                        Accepted = enqueueResult.Accepted
                    };
                    if (!string.IsNullOrEmpty(enqueueResult.Error))
                    {
                        resp.Error = "Could not enqueue."; // keep terse for now
                    }
                    return new InternalEnvelope { EnqueueOpaqueMessageResponse = resp };
                }
                case MessageQueueEnvelope.MessageOneofCase.FetchQueuedMessagesRequest:
                {
                    var req = mq.FetchQueuedMessagesRequest;
                    int requestedMax = req.HasMaxCount ? (int)req.MaxCount : 100;
                    requestedMax = Math.Clamp(requestedMax, 1, 500);

                    if (request.Context.RemotePeerGuid is null)
                    {
                        _logger.LogWarning("FetchQueuedMessagesRequest missing RemotePeerGuid in context");
                        return null;
                    }
                    var fetchResult = await _mediator.Send(
                        new FetchQueuedMessagesQuery(new PeerId(request.Context.RemotePeerGuid.Value), requestedMax),
                        cancellationToken).ConfigureAwait(false);

                    var resp = new FetchQueuedMessagesResponse();
                    resp.Messages.AddRange(fetchResult.Messages.Select(ByteString.CopyFrom));
                    return new InternalEnvelope { FetchQueuedMessagesResponse = resp };
                }
                default:
                    return null;
            }
        }

        // No-op by default; caller continues local handling.
        return null;
    }
}
