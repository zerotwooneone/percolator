using System.Security.Cryptography;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Sessions;
using Percolator.Application.Identity;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Network;
using Percolator.Prekey.Handlers;
using Percolator.Identity;
using Percolator.MessageQueue.Commands;
using Percolator.Chat.App;
using Percolator.Chat.App.Commands;
using Percolator.Chat.ValueObjects;
using Percolator.Chat.Primitives;
using Percolator.Application.Apps.Chat;
using Percolator.Application.Network.Handshake;
using CryptoPeerId = Percolator.Cryptography.Primitives.PeerId;
using IdentityPeerId = Percolator.Identity.PeerId;
using NetworkPeerId = Percolator.Network.PeerId;

namespace Percolator.Application.Network
{
    public class DeliverOpaqueMessageHandler : IRequestHandler<DeliverOpaqueMessageCommand, DeliverOpaqueMessageResult>
    {
        private readonly ILogger<DeliverOpaqueMessageHandler> _logger;
        private readonly IDirectSessionManager _sessionManager;
        private readonly IPeerConnectionRepository _peerConnectionRepository;
        private readonly IMediator _mediator;
        private readonly IDirectSessionRepository _directSessionRepository;
        private readonly ActiveIdentityContext _activeIdentityContext;
        private readonly IRatchetKeySessionLookup _ratchetLookup;
        
        public DeliverOpaqueMessageHandler(
            ILogger<DeliverOpaqueMessageHandler> logger,
            IDirectSessionManager sessionManager,
            IPeerConnectionRepository peerConnectionRepository,
            IMediator mediator,
            IDirectSessionRepository directSessionRepository,
            ActiveIdentityContext activeIdentityContext,
            IRatchetKeySessionLookup ratchetLookup)
        {
            _logger = logger;
            _sessionManager = sessionManager;
            _peerConnectionRepository = peerConnectionRepository;
            _mediator = mediator;
            _directSessionRepository = directSessionRepository;
            _activeIdentityContext = activeIdentityContext;
            _ratchetLookup = ratchetLookup;
        }

        private async Task<SubmitPreKeyBundleResponse> HandlePrekeyEnvelopeAsync(PrekeyEnvelope prekeyEnvelope,
            NetworkPeerId remotePeerId, CancellationToken ct)
        {
            switch (prekeyEnvelope.MessageCase)
            {
                case PrekeyEnvelope.MessageOneofCase.SubmitPreKeyBundleRequest:
                    var upload = prekeyEnvelope.SubmitPreKeyBundleRequest;
                    if (!upload.HasIdentityKey)
                    {
                        throw new InvalidOperationException("Identity key is required");
                    }
                    if (!upload.HasSignedPreKeyId)
                    {
                        throw new InvalidOperationException("Signed pre-key ID is required");
                    }
                    if (!upload.HasSignedPreKey)
                    {
                        throw new InvalidOperationException("Signed pre-key is required");
                    }
                    if (!upload.HasPreKeySignature)
                    {
                        throw new InvalidOperationException("Pre-key signature is required");
                    }

                    if (upload.OneTimePreKeys.Count == 0)
                    {
                        throw new InvalidOperationException("At least one one-time pre-key is required");
                    }
                    const int maxBundles = 100;
                    if(upload.OneTimePreKeys.Count > maxBundles)
                    {
                        throw new InvalidOperationException($"Too many one-time pre-keys. Maximum is {maxBundles}");
                    }
                    foreach (var oneTimePreKey in upload.OneTimePreKeys)
                    {
                        if (!oneTimePreKey.HasId)
                        {
                            throw new InvalidOperationException("One-time pre-key is required");
                        }

                        if (!oneTimePreKey.HasPublicKey)
                        {
                            throw new InvalidOperationException("One-time pre-key public key is required");
                        }
                    }
                    if (upload.ExpiresUtc.ToDateTimeOffset() < DateTimeOffset.Now)
                    {
                        throw new InvalidOperationException("Pre-key bundle has expired");
                    }
                    var cmd = new SubmitPreKeyBundleCommand
                    {
                        PublicSigningKey = upload.IdentityKey.ToByteArray(),
                        SignedPreKeyId = new Guid(upload.SignedPreKeyId.ToByteArray()),
                        SignedPreKey = upload.SignedPreKey.ToByteArray(),
                        PreKeySignature = upload.PreKeySignature.ToByteArray(),
                        OneTimePreKeys = upload.OneTimePreKeys.Select(x => new SubmitPreKeyBundleCommand.OneTimePreKey(
                            new Guid(x.Id.ToByteArray()), x.PublicKey.ToByteArray())).ToList(),
                        Expires = upload.ExpiresUtc.ToDateTimeOffset(),
                        RemotePeerId = remotePeerId
                    };
                    await _mediator.Send(cmd, ct);
                    break;
                default:
                    _logger.LogWarning("Received unhandled prekey message type: {MessageType}", prekeyEnvelope.MessageCase);
                    break;
            }

            return new SubmitPreKeyBundleResponse();
        }

        public async Task<DeliverOpaqueMessageResult> Handle(DeliverOpaqueMessageCommand request, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Processing opaque message (session inferred from ratchet header)");
            try
            {
                // Parse and infer session from ratchet header key
                var sessionRatchetMessage = new SessionRatchetMessage(request.PayloadBytes);
                // Infer session by ratchet header key (PreKey)
                var header = sessionRatchetMessage.GetHeader();
                var ratchetKey = header.PreKey;
                var resolvedDirectSessionId = await _ratchetLookup.TryResolveAsync(ratchetKey, _activeIdentityContext.Identity!.SelfIdentityId, cancellationToken);
                Plaintext? plaintext;
                SessionId inferredSessionId;
                DirectSessionId nonNullDirectSessionId;
                if (resolvedDirectSessionId is not null)
                {
                    // Fast path
                    nonNullDirectSessionId = resolvedDirectSessionId.Value;
                    inferredSessionId = new SessionId(nonNullDirectSessionId.Value);
                    _logger.LogDebug("Fast-path lookup hit for ratchet header key; inferred session {SessionId}", inferredSessionId);
                    plaintext = await _sessionManager.ReceiveMessageAsync(inferredSessionId, sessionRatchetMessage);
                }
                else
                {
                    // Slow path: trial decrypt to infer session
                    _logger.LogWarning("Fast-path lookup MISS for ratchet header key; attempting slow-path inference");
                    var inferResult = await _sessionManager.TryInferAndReceiveAsync(sessionRatchetMessage, cancellationToken)
                        ?? throw new InvalidOperationException("Unable to resolve session by ratchet header key or slow-path inference");
                    inferredSessionId = inferResult.sessionId;
                    plaintext = inferResult.plaintext;
                    _logger.LogInformation("Slow-path inference SUCCEEDED; inferred session {SessionId}", inferredSessionId);
                    // We still need a DirectSessionId for repository lookups; derive from inferredSessionId
                    nonNullDirectSessionId = new DirectSessionId(inferredSessionId.Value);
                }
                if (plaintext is null)
                {
                    _logger.LogWarning("Decryption resulted in null plaintext for session {SessionId}. This may be a skipped message.", inferredSessionId);
                    return new DeliverOpaqueMessageResult();
                }

                // Keep ratchet-key index fresh for fast lookups
                await _ratchetLookup.UpsertAsync(nonNullDirectSessionId, _activeIdentityContext.Identity!.SelfIdentityId, ratchetKey, DateTimeOffset.UtcNow, cancellationToken);

                // Now that we have the inferred session, resolve the remote peer/connection info
                var directSession = await _directSessionRepository.GetBySessionIdAsync(nonNullDirectSessionId, _activeIdentityContext.Identity.SelfIdentityId);
                if (directSession is null)
                {
                    throw new InvalidOperationException($"No direct session mapping found for session {inferredSessionId}");
                }
                var remotePeerId = directSession.RemotePeerId;
                _logger.LogInformation("Resolved remote peer {PeerId} for session {SessionId}", remotePeerId, directSession.SessionId);
                var connectionInfo = await _peerConnectionRepository.GetByIdAsync(remotePeerId);
                if (connectionInfo?.GrpcEndPoints.FirstOrDefault() is null)
                {
                    _logger.LogWarning("Could not find connection info for peer {PeerId} to handle opaque message", remotePeerId);
                    return new DeliverOpaqueMessageResult();
                }

                //todo: find a better way to find the current endpoint
                var endpoint = connectionInfo.GrpcEndPoints.First();
                _logger.LogInformation("Using endpoint {Endpoint} for peer {PeerId}", endpoint, remotePeerId);

                var internalEnvelope = InternalEnvelope.Parser.ParseFrom(plaintext.Value);
                InternalEnvelope? responseEnvelope = null;
                
                connectionInfo.UpdateLastSeen(endpoint, DateTimeOffset.UtcNow);
                await _peerConnectionRepository.SaveAsync(connectionInfo);

                switch (internalEnvelope.ApplicationPayloadCase)
                {
                    case InternalEnvelope.ApplicationPayloadOneofCase.ChatEnvelope:
                        await HandleChatEnvelopeAsync(internalEnvelope.ChatEnvelope, directSession.SessionId, cancellationToken);
                        break;
                    case InternalEnvelope.ApplicationPayloadOneofCase.DhtEnvelope:
                        responseEnvelope = await HandleDhtMessageAsync(internalEnvelope.DhtEnvelope, connectionInfo, endpoint, cancellationToken);
                        break;
                    case InternalEnvelope.ApplicationPayloadOneofCase.PrekeyEnvelope:
                        var response = await HandlePrekeyEnvelopeAsync(internalEnvelope.PrekeyEnvelope, connectionInfo.Id, cancellationToken);
                        responseEnvelope = new InternalEnvelope { SubmitPreKeyBundleResponse = response };
                        break;
                    case InternalEnvelope.ApplicationPayloadOneofCase.HandshakeInitiatorHello:
                    {
                        var hello = internalEnvelope.HandshakeInitiatorHello;
                        var spki = hello.InitiatorIdentityKeySpki.ToByteArray();
                        var eph = hello.InitiatorEphemeralKeySpki.ToByteArray();
                        var spkId = new Guid(hello.SignedPreKeyId.ToByteArray());
                        Guid? otkId = hello.HasOneTimePreKeyId ? new Guid(hello.OneTimePreKeyId.ToByteArray()) : (Guid?)null;

                        var resultEnv = await _mediator.Send(
                            new Percolator.Application.Network.Handshake.HandleHandshakeInitiatorHelloCommand(
                                spki,
                                eph,
                                spkId,
                                otkId,
                                new IdentityPeerId(remotePeerId.Value)),
                            cancellationToken);
                        if (resultEnv is not null)
                        {
                            responseEnvelope = resultEnv;
                        }
                        break;
                    }
                    case InternalEnvelope.ApplicationPayloadOneofCase.MessageQueueEnvelope:
                        switch (internalEnvelope.MessageQueueEnvelope.MessageCase)
                        {
                            case MessageQueueEnvelope.MessageOneofCase.EnqueueOpaqueMessageRequest:
                                var mqReq = internalEnvelope.MessageQueueEnvelope.EnqueueOpaqueMessageRequest;
                                var enqueueResult = await _mediator.Send(new EnqueueOpaqueMessageCommand(
                                    mqReq.RecipientPublicKeyHash.ToByteArray(),
                                    mqReq.MessageBlob.ToByteArray()
                                ), cancellationToken);

                                var enqueueResponse = new EnqueueOpaqueMessageResponse
                                {
                                    Accepted = enqueueResult.Accepted
                                };
                                if (!string.IsNullOrEmpty(enqueueResult.Error))
                                {
                                    enqueueResponse.Error = "Could not enqueue."; //enqueueResult.Error;
                                }

                                responseEnvelope = new InternalEnvelope { EnqueueOpaqueMessageResponse = enqueueResponse };
                                break;
                            case MessageQueueEnvelope.MessageOneofCase.FetchQueuedMessagesRequest:
                                var fetchReq = internalEnvelope.MessageQueueEnvelope.FetchQueuedMessagesRequest;
                                // Default and cap max count
                                int requestedMax = fetchReq.HasMaxCount ? (int)fetchReq.MaxCount : 100;
                                requestedMax = Math.Clamp(requestedMax, 1, 500);

                                var fetchResult = await _mediator.Send(
                                    new FetchQueuedMessagesQuery(new Percolator.Identity.PeerId(remotePeerId.Value), requestedMax),
                                    cancellationToken);

                                var fetchResp = new FetchQueuedMessagesResponse
                                {
                                };
                                fetchResp.Messages.AddRange(fetchResult.Messages.Select(ByteString.CopyFrom));

                                responseEnvelope = new InternalEnvelope { FetchQueuedMessagesResponse = fetchResp };
                                break;
                            default:
                                _logger.LogWarning("Received unhandled MessageQueue message type: {MessageType}", internalEnvelope.MessageQueueEnvelope.MessageCase);
                                break;
                        }
                        break;
                    case InternalEnvelope.ApplicationPayloadOneofCase.RelayOpaqueEnvelope:
                        // Forward opaque payload to client-side processor; do not parse here.
                        var relay = internalEnvelope.RelayOpaqueEnvelope;
                        await _mediator.Send(new ProcessRelayedOpaquePayloadCommand(relay.OpaquePayload.ToByteArray()), cancellationToken);
                        break;
                    default:
                        _logger.LogWarning("Received unhandled internal envelope type: {EnvelopeType}", internalEnvelope.ApplicationPayloadCase);
                        break;
                }

                if (responseEnvelope is null)
                {
                    return new DeliverOpaqueMessageResult();
                }

                var responseBytes = await EncryptResponseEnvelope(inferredSessionId, responseEnvelope);
                return new DeliverOpaqueMessageResult { ResponsePayloadBytes = responseBytes };

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing opaque message (session inferred from ratchet header)");
                throw;
            }
        }

        private async Task HandleChatEnvelopeAsync(ChatEnvelope chatEnvelope, DirectSessionId directSessionId, CancellationToken ct)
        {
            ConversationLookupKey BuildLookupKey(Guid? groupGuid, byte[]? pkh)
            {
                if (groupGuid.HasValue)
                {
                    return ConversationLookupKey.ForGroup(groupGuid.Value);
                }
                if (pkh is not null && pkh.Length > 0)
                {
                    return ConversationLookupKey.ForPublicKeyHash(Pkh.FromBytes(pkh));
                }
                return ConversationLookupKey.ForDirectSession(directSessionId.Value);
            }

            static void ValidateRoutingHints(Guid? groupGuid, byte[]? pkh, string context)
            {
                int count = 0;
                if (groupGuid.HasValue) count++;
                if (pkh is not null && pkh.Length > 0) count++;
                if (count > 1)
                {
                    throw new InvalidOperationException($"{context}: exactly one of group_conversation_guid or public_key_hash may be present.");
                }
            }

            // Local handler for group membership updates
            async Task HandleUpdateGroupMembershipAsync(UpdateGroupMembershipRequest ugr)
            {
                if (!ugr.HasGroupConversationGuid)
                {
                    throw new InvalidOperationException("UpdateGroupMembershipRequest.group_conversation_guid is required.");
                }
                if (ugr.GroupConversationGuid.Length != 16)
                {
                    throw new InvalidOperationException("UpdateGroupMembershipRequest.group_conversation_guid must be 16 bytes (GUID).");
                }

                var groupGuid = new Guid(ugr.GroupConversationGuid.ToByteArray());
                var lookup = BuildLookupKey(groupGuid, null);

                var toAdd = new List<Percolator.Chat.ValueObjects.ParticipantId>(ugr.MembersToAdd.Count);
                foreach (var b in ugr.MembersToAdd)
                {
                    if (b.Length != 16)
                    {
                        throw new InvalidOperationException("UpdateGroupMembershipRequest.members_to_add must be GUID bytes (16).");
                    }
                    toAdd.Add(new Percolator.Chat.ValueObjects.ParticipantId(new Guid(b.ToByteArray())));
                }

                var toRemove = new List<Percolator.Chat.ValueObjects.ParticipantId>(ugr.MembersToRemove.Count);
                foreach (var b in ugr.MembersToRemove)
                {
                    if (b.Length != 16)
                    {
                        throw new InvalidOperationException("UpdateGroupMembershipRequest.members_to_remove must be GUID bytes (16).");
                    }
                    toRemove.Add(new Percolator.Chat.ValueObjects.ParticipantId(new Guid(b.ToByteArray())));
                }

                await _mediator.Send(new UpdateGroupMembershipCommand(
                    lookup,
                    toAdd,
                    toRemove,
                    ugr.LeaveGroup
                ), ct);
            }

            // Local handler for group info updates
            async Task HandleUpdateGroupInfoAsync(UpdateGroupInfoRequest ugi)
            {
                if (!ugi.HasGroupConversationGuid)
                {
                    throw new InvalidOperationException("UpdateGroupInfoRequest.group_conversation_guid is required.");
                }
                if (ugi.GroupConversationGuid.Length != 16)
                {
                    throw new InvalidOperationException("UpdateGroupInfoRequest.group_conversation_guid must be 16 bytes (GUID).");
                }

                var groupGuid = new Guid(ugi.GroupConversationGuid.ToByteArray());
                var lookup = BuildLookupKey(groupGuid, null);
                var newName = ugi.HasNewGroupName ? ugi.NewGroupName : null;

                await _mediator.Send(new UpdateGroupInfoCommand(lookup, newName), ct);
            }

            switch (chatEnvelope.MessageCase)
            {
                case ChatEnvelope.MessageOneofCase.TextMessage:
                    var text = chatEnvelope.TextMessage;
                    // Sanity checks (Application layer): ensure required fields are present and valid
                    if (text.MessageId == null || text.MessageId.Length == 0)
                    {
                        throw new InvalidOperationException("TextMessage.message_id is required.");
                    }
                    if (text.MessageId.Length != 16)
                    {
                        throw new InvalidOperationException("TextMessage.message_id must be 16 bytes (GUID).");
                    }
                    Guid? groupGuid = null;
                    if (text.HasGroupConversationGuid)
                    {
                        if (text.GroupConversationGuid.Length != 16)
                        {
                            throw new InvalidOperationException("TextMessage.group_conversation_guid must be 16 bytes (GUID) when present.");
                        }
                        groupGuid = new Guid(text.GroupConversationGuid.ToByteArray());
                    }
                    byte[]? pkh = null;
                    if (text.HasPublicKeyHash)
                    {
                        if (text.PublicKeyHash.Length != 32)
                        {
                            throw new InvalidOperationException("TextMessage.public_key_hash must be 32 bytes (SHA-256) when present.");
                        }
                        pkh = text.PublicKeyHash.ToByteArray();
                    }
                    ValidateRoutingHints(groupGuid, pkh, "TextMessage");
                    var lookupKey = BuildLookupKey(groupGuid, pkh);
                    var messageId = new MessageId(new Guid(text.MessageId.ToByteArray()));
                    var sentTs = text.SentTimestampUtc.ToDateTimeOffset();
                    await _mediator.Send(new PostTextMessageCommand(
                        lookupKey,
                        messageId,
                        text.Content,
                        sentTs
                    ), ct);
                    break;
                case ChatEnvelope.MessageOneofCase.ReadReceipt:
                    var rr = chatEnvelope.ReadReceipt;
                    if (rr.MessageId == null || rr.MessageId.Length == 0)
                    {
                        throw new InvalidOperationException("ReadReceipt.message_id is required.");
                    }
                    if (rr.MessageId.Length != 16)
                    {
                        throw new InvalidOperationException("ReadReceipt.message_id must be 16 bytes (GUID).");
                    }
                    Guid? rrGroupGuid = null;
                    if (rr.HasGroupConversationGuid)
                    {
                        if (rr.GroupConversationGuid.Length != 16)
                        {
                            throw new InvalidOperationException("ReadReceipt.group_conversation_guid must be 16 bytes (GUID) when present.");
                        }
                        rrGroupGuid = new Guid(rr.GroupConversationGuid.ToByteArray());
                    }
                    byte[]? rrPkh = null;
                    if (rr.HasPublicKeyHash)
                    {
                        if (rr.PublicKeyHash.Length != 32)
                        {
                            throw new InvalidOperationException("ReadReceipt.public_key_hash must be 32 bytes (SHA-256) when present.");
                        }
                        rrPkh = rr.PublicKeyHash.ToByteArray();
                    }
                    ValidateRoutingHints(rrGroupGuid, rrPkh, "ReadReceipt");
                    var rrLookup = BuildLookupKey(rrGroupGuid, rrPkh);
                    var rrMessageId = new MessageId(new Guid(rr.MessageId.ToByteArray()));
                    var rrTs = rr.SentTimestampUtc.ToDateTimeOffset();
                    await _mediator.Send(new PostReadReceiptCommand(
                        rrLookup,
                        rrMessageId,
                        rrTs
                    ), ct);
                    break;
                case ChatEnvelope.MessageOneofCase.EmojiAnnotation:
                    var em = chatEnvelope.EmojiAnnotation;
                    if (em.MessageId == null || em.MessageId.Length == 0)
                    {
                        throw new InvalidOperationException("EmojiAnnotation.message_id is required.");
                    }
                    if (em.MessageId.Length != 16)
                    {
                        throw new InvalidOperationException("EmojiAnnotation.message_id must be 16 bytes (GUID).");
                    }
                    if (string.IsNullOrWhiteSpace(em.Emoji))
                    {
                        throw new InvalidOperationException("EmojiAnnotation.emoji is required.");
                    }
                    Guid? emGroupGuid = null;
                    if (em.HasGroupConversationGuid)
                    {
                        if (em.GroupConversationGuid.Length != 16)
                        {
                            throw new InvalidOperationException("EmojiAnnotation.group_conversation_guid must be 16 bytes (GUID) when present.");
                        }
                        emGroupGuid = new Guid(em.GroupConversationGuid.ToByteArray());
                    }
                    byte[]? emPkh = null;
                    if (em.HasPublicKeyHash)
                    {
                        if (em.PublicKeyHash.Length != 32)
                        {
                            throw new InvalidOperationException("EmojiAnnotation.public_key_hash must be 32 bytes (SHA-256) when present.");
                        }
                        emPkh = em.PublicKeyHash.ToByteArray();
                    }
                    ValidateRoutingHints(emGroupGuid, emPkh, "EmojiAnnotation");
                    var emLookup = BuildLookupKey(emGroupGuid, emPkh);
                    var emMessageId = new MessageId(new Guid(em.MessageId.ToByteArray()));
                    var emTs = em.SentTimestampUtc.ToDateTimeOffset();
                    await _mediator.Send(new PostEmojiAnnotationCommand(
                        emLookup,
                        emMessageId,
                        em.Emoji,
                        emTs
                    ), ct);
                    break;
                case ChatEnvelope.MessageOneofCase.UpdateGroupMembershipRequest:
                    await HandleUpdateGroupMembershipAsync(chatEnvelope.UpdateGroupMembershipRequest);
                    break;
                case ChatEnvelope.MessageOneofCase.UpdateGroupInfoRequest:
                    await HandleUpdateGroupInfoAsync(chatEnvelope.UpdateGroupInfoRequest);
                    break;
                case ChatEnvelope.MessageOneofCase.DeliveredReceipt:
                    var dr = chatEnvelope.DeliveredReceipt;
                    if (dr.MessageId == null || dr.MessageId.Length == 0)
                    {
                        throw new InvalidOperationException("DeliveredReceipt.message_id is required.");
                    }
                    if (dr.MessageId.Length != 16)
                    {
                        throw new InvalidOperationException("DeliveredReceipt.message_id must be 16 bytes (GUID).");
                    }
                    Guid? drGroupGuid = null;
                    if (dr.HasGroupConversationGuid)
                    {
                        if (dr.GroupConversationGuid.Length != 16)
                        {
                            throw new InvalidOperationException("DeliveredReceipt.group_conversation_guid must be 16 bytes (GUID) when present.");
                        }
                        drGroupGuid = new Guid(dr.GroupConversationGuid.ToByteArray());
                    }
                    byte[]? drPkh = null;
                    if (dr.HasPublicKeyHash)
                    {
                        if (dr.PublicKeyHash.Length != 32)
                        {
                            throw new InvalidOperationException("DeliveredReceipt.public_key_hash must be 32 bytes (SHA-256) when present.");
                        }
                        drPkh = dr.PublicKeyHash.ToByteArray();
                    }
                    ValidateRoutingHints(drGroupGuid, drPkh, "DeliveredReceipt");
                    var drLookup = BuildLookupKey(drGroupGuid, drPkh);
                    var drMessageId = new MessageId(new Guid(dr.MessageId.ToByteArray()));
                    var drTs = dr.SentTimestampUtc.ToDateTimeOffset();
                    await _mediator.Send(new PostDeliveredReceiptCommand(
                        drLookup,
                        drMessageId,
                        drTs
                    ), ct);
                    break;
                case ChatEnvelope.MessageOneofCase.SignedAdminOperation:
                    var sao = chatEnvelope.SignedAdminOperation;
                    if (sao == null || sao.Payload == null || !sao.HasSignature)
                    {
                        throw new InvalidOperationException("SignedAdminOperation.payload and signature are required.");
                    }
                    if (!sao.Payload.HasGroupConversationGuid || sao.Payload.GroupConversationGuid.Length != 16)
                    {
                        throw new InvalidOperationException("SignedAdminOperation.payload.group_conversation_guid must be 16 bytes (GUID).");
                    }
                    if (!sao.Payload.HasOpId || sao.Payload.OpId.Length != 16)
                    {
                        throw new InvalidOperationException("SignedAdminOperation.payload.op_id must be 16 bytes (GUID).");
                    }
                    var saoGroup = new Guid(sao.Payload.GroupConversationGuid.ToByteArray());
                    var saoLookup = BuildLookupKey(saoGroup, null);

                    // Common fields
                    var opId = new Guid(sao.Payload.OpId.ToByteArray());
                    var sentUtc = sao.Payload.SentTimestampUtc.ToDateTimeOffset();
                    ulong? adminSeq = sao.Payload.HasAdminSequenceNumber ? sao.Payload.AdminSequenceNumber : null;

                    // Map variant-specific data
                    AdminOperationKind kind;
                    AdminPublicKey? grantee = null;
                    List<Percolator.Chat.ValueObjects.ParticipantId>? add = null;
                    List<Percolator.Chat.ValueObjects.ParticipantId>? remove = null;
                    bool? leave = null;
                    string? newName2 = null;
                    Percolator.Chat.Primitives.GroupAvatar? newAvatar2 = null;

                    switch (sao.Payload.OperationCase)
                    {
                        case AdminOperationPayload.OperationOneofCase.GrantAdmin:
                            kind = AdminOperationKind.GrantAdmin;
                            if (!sao.Payload.GrantAdmin.HasGranteePublicKey)
                                throw new InvalidOperationException("GrantAdmin.grantee_public_key is required.");
                            grantee = new AdminPublicKey(sao.Payload.GrantAdmin.GranteePublicKey.ToByteArray());
                            break;
                        case AdminOperationPayload.OperationOneofCase.RevokeAdmin:
                            kind = AdminOperationKind.RevokeAdmin;
                            if (!sao.Payload.RevokeAdmin.HasGranteePublicKey)
                                throw new InvalidOperationException("RevokeAdmin.grantee_public_key is required.");
                            grantee = new AdminPublicKey(sao.Payload.RevokeAdmin.GranteePublicKey.ToByteArray());
                            break;
                        case AdminOperationPayload.OperationOneofCase.UpdateGroupMembership:
                            kind = AdminOperationKind.UpdateGroupMembership;
                            add = new List<Percolator.Chat.ValueObjects.ParticipantId>(sao.Payload.UpdateGroupMembership.MembersToAdd.Count);
                            foreach (var b in sao.Payload.UpdateGroupMembership.MembersToAdd)
                            {
                                if (b.Length != 16) throw new InvalidOperationException("members_to_add must be 16-byte GUIDs");
                                add.Add(new Percolator.Chat.ValueObjects.ParticipantId(new Guid(b.ToByteArray())));
                            }
                            remove = new List<Percolator.Chat.ValueObjects.ParticipantId>(sao.Payload.UpdateGroupMembership.MembersToRemove.Count);
                            foreach (var b in sao.Payload.UpdateGroupMembership.MembersToRemove)
                            {
                                if (b.Length != 16) throw new InvalidOperationException("members_to_remove must be 16-byte GUIDs");
                                remove.Add(new Percolator.Chat.ValueObjects.ParticipantId(new Guid(b.ToByteArray())));
                            }
                            leave = sao.Payload.UpdateGroupMembership.HasLeaveGroup ? sao.Payload.UpdateGroupMembership.LeaveGroup : (bool?)null;
                            break;
                        case AdminOperationPayload.OperationOneofCase.UpdateGroupInfo:
                            kind = AdminOperationKind.UpdateGroupInfo;
                            if (sao.Payload.UpdateGroupInfo.HasNewGroupName)
                            {
                                newName2 = sao.Payload.UpdateGroupInfo.NewGroupName;
                            }
                            if (sao.Payload.UpdateGroupInfo.HasNewGroupAvatar)
                            {
                                newAvatar2 = new Percolator.Chat.Primitives.GroupAvatar(sao.Payload.UpdateGroupInfo.NewGroupAvatar.ToByteArray());
                            }
                            break;
                        default:
                            throw new InvalidOperationException($"Unsupported admin operation variant: {sao.Payload.OperationCase}");
                    }

                    var signature = new AdminSignature(sao.Signature.ToByteArray());

                    await _mediator.Send(new ApplySignedAdminOperationCommand(
                        saoLookup,
                        opId,
                        sentUtc,
                        adminSeq,
                        kind,
                        grantee,
                        add,
                        remove,
                        leave,
                        newName2,
                        newAvatar2,
                        signature,
                        CanonicalPayload.ForAdminOperation(sao.Payload)
                    ), ct);
                    break;

                case ChatEnvelope.MessageOneofCase.KeyAdoptionConfirmation:
                    var kac = chatEnvelope.KeyAdoptionConfirmation;
                    if (kac == null || !kac.HasGroupConversationGuid || kac.GroupConversationGuid.Length != 16)
                    {
                        throw new InvalidOperationException("SignedKeyAdoptionConfirmation.group_conversation_guid must be 16 bytes (GUID).");
                    }
                    if (!kac.HasKeyVersion)
                    {
                        throw new InvalidOperationException("SignedKeyAdoptionConfirmation.key_version is required.");
                    }
                    if (!kac.HasAdopterIdentityKey || kac.AdopterIdentityKey.Length == 0)
                    {
                        throw new InvalidOperationException("SignedKeyAdoptionConfirmation.adopter_identity_key is required.");
                    }
                    if (!kac.HasSignature || kac.Signature.Length == 0)
                    {
                        throw new InvalidOperationException("SignedKeyAdoptionConfirmation.signature is required.");
                    }
                    var kacGroup = new Guid(kac.GroupConversationGuid.ToByteArray());
                    var kacLookup = BuildLookupKey(kacGroup, null);
                    await _mediator.Send(new ReceiveKeyAdoptionConfirmationCommand(
                        kacLookup,
                        new GroupKeyVersion(kac.KeyVersion),
                        new IdentityPublicKey(kac.AdopterIdentityKey.ToByteArray()),
                        kac.SentTimestampUtc.ToDateTimeOffset(),
                        kac.Signature.ToByteArray()
                    ), ct);
                    break;

                case ChatEnvelope.MessageOneofCase.AdminCommitOperation:
                    var aco = chatEnvelope.AdminCommitOperation;
                    if (aco == null || !aco.HasGroupConversationGuid || aco.GroupConversationGuid.Length != 16)
                    {
                        throw new InvalidOperationException("SignedAdminCommitOperation.group_conversation_guid must be 16 bytes (GUID).");
                    }
                    if (!aco.HasOpId || aco.OpId.Length != 16)
                    {
                        throw new InvalidOperationException("SignedAdminCommitOperation.op_id must be 16 bytes (GUID).");
                    }
                    if (!aco.HasCommittedKeyVersion)
                    {
                        throw new InvalidOperationException("SignedAdminCommitOperation.committed_key_version is required.");
                    }
                    if (!aco.HasSignature || aco.Signature.Length == 0)
                    {
                        throw new InvalidOperationException("SignedAdminCommitOperation.signature is required.");
                    }
                    if (!aco.HasAdminSequenceNumber)
                    {
                        throw new InvalidOperationException("SignedAdminCommitOperation.admin_sequence_number is required.");
                    }
                    var acoGroup = new Guid(aco.GroupConversationGuid.ToByteArray());
                    var acoLookup = BuildLookupKey(acoGroup, null);
                    await _mediator.Send(new ReceiveAdminCommitCommand(
                        acoLookup,
                        new Guid(aco.OpId.ToByteArray()),
                        new GroupKeyVersion(aco.CommittedKeyVersion),
                        aco.SentTimestampUtc.ToDateTimeOffset(),
                        aco.AdminSequenceNumber,
                        aco.Signature.ToByteArray()
                    ), ct);
                    break;

                case ChatEnvelope.MessageOneofCase.KeyDistribution:
                    var kd = chatEnvelope.KeyDistribution;
                    if (kd == null || !kd.HasGroupConversationGuid || kd.GroupConversationGuid.Length != 16)
                    {
                        throw new InvalidOperationException("KeyDistributionPayload.group_conversation_guid must be 16 bytes (GUID).");
                    }
                    if (!kd.HasKeyVersion)
                    {
                        throw new InvalidOperationException("KeyDistributionPayload.key_version is required.");
                    }
                    if (!kd.HasEncryptedGroupKeyForRecipient || kd.EncryptedGroupKeyForRecipient.Length == 0)
                    {
                        throw new InvalidOperationException("KeyDistributionPayload.encrypted_group_key_for_recipient is required.");
                    }
                    var kdGroup = new Guid(kd.GroupConversationGuid.ToByteArray());
                    var kdLookup = BuildLookupKey(kdGroup, null);
                    await _mediator.Send(new ReceiveKeyDistributionCommand(
                        kdLookup,
                        new GroupKeyVersion(kd.KeyVersion),
                        new EncryptedGroupKey(kd.EncryptedGroupKeyForRecipient.ToByteArray())
                    ), ct);
                    break;

                default:
                    _logger.LogWarning("Received unhandled chat message type: {MessageType}", chatEnvelope.MessageCase);
                    break;
            }
        }

        private async Task<InternalEnvelope?> HandleDhtMessageAsync(DhtEnvelope dhtEnvelope, PeerConnection peerConnection, GrpcEndPoint endPoint, CancellationToken ct)
        {
            if (_activeIdentityContext.Identity is null)
            {
                _logger.LogError("No active identity available");
                return null;
            }
            // Resolve the remote peer from the direct session mapping

            switch (dhtEnvelope.MessageCase)
            {
                //todo: avoid sending mediatR requests from mediatR handlers
                case DhtEnvelope.MessageOneofCase.PingRequest:
                    if (peerConnection.IdentitySigningKey is null)
                    {
                        _logger.LogWarning("Could not find identity signing key for peer {PeerId} to handle DHT message", peerConnection.Id);
                        return null;
                    }
                    // NodeId is defined as SHA-256 digest of the peer's SPKI signing key bytes (32 bytes)
                    var nodeIdBytes = System.Security.Cryptography.SHA256.HashData(peerConnection.IdentitySigningKey.Value);
                    await _mediator.Send(new Percolator.Dht.Messages.PingRequest(new Percolator.Dht.NodeId(nodeIdBytes), endPoint.EndPoint), ct);
                    break;
                case DhtEnvelope.MessageOneofCase.FindNodeRequest:
                    //todo: prevent finding nodes if the peer has not given us prekey bundles
                    var findNodeResponse = await _mediator.Send(new Percolator.Dht.Messages.FindNodeRequest(new Percolator.Dht.NodeId(dhtEnvelope.FindNodeRequest.TargetPeerId.ToByteArray())), ct);
                    var responseEnvelope = new InternalEnvelope
                    {
                        DhtEnvelope = new DhtEnvelope
                        {
                            FindNodeResponse = new Contracts.FindNodeResponse()
                        }
                    };
                    responseEnvelope.DhtEnvelope.FindNodeResponse.CloserPeers.AddRange(findNodeResponse.CloserNodes.Select(n =>
                        new NodeInfo
                        {
                            PeerId = ByteString.CopyFrom(n.Id.Value),
                            Address = n.EndPoint.ToString()
                        }));

                    return responseEnvelope;
                default:
                    _logger.LogWarning("Received unhandled DHT message type: {MessageType}", dhtEnvelope.MessageCase);
                    break;
            }

            return null;
        }

        private async Task<byte[]> EncryptResponseEnvelope(SessionId sessionId, InternalEnvelope internalEnvelope)
        {
            var plaintext = new Plaintext(internalEnvelope.ToByteArray());
            var ratchetMessage = await _sessionManager.EncryptMessageAsync(sessionId, plaintext);
            return ratchetMessage.Value;
        }
    }
}
