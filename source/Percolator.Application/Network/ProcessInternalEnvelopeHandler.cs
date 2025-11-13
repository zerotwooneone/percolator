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
using Percolator.MessageQueue.Commands;
using Percolator.MessageQueue.Abstractions;
using Percolator.MessageQueue.Primitives;
using Percolator.Network;
using Percolator.Prekey.Handlers;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Network;

internal sealed class ProcessInternalEnvelopeHandler : IRequestHandler<ProcessInternalEnvelopeCommand, InternalEnvelope?>
{
    private readonly ILogger<ProcessInternalEnvelopeHandler> _logger;
    private readonly IMediator _mediator;
    private readonly Percolator.Chat.App.IAdminOperations _adminOps;
    private readonly IDhtService _dhtService;
    private readonly IPeerConnectionRepository _peerConnectionRepository;
    private readonly IMessageQueueService _mqService;
    public ProcessInternalEnvelopeHandler(ILogger<ProcessInternalEnvelopeHandler> logger, IMediator mediator, Percolator.Chat.App.IAdminOperations adminOps, IDhtService dhtService, IPeerConnectionRepository peerConnectionRepository, IMessageQueueService mqService)
    {
        _logger = logger;
        _mediator = mediator;
        _adminOps = adminOps;
        _dhtService = dhtService;
        _peerConnectionRepository = peerConnectionRepository;
        _mqService = mqService;
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
                var target = dht.FindNodeRequest.HasTargetPeerId ? dht.FindNodeRequest.TargetPeerId.ToByteArray() : Array.Empty<byte>();
                if (target.Length == 0)
                {
                    _logger.LogWarning("FindNodeRequest missing target_peer_id");
                    return null;
                }

                var closerNodes = await _dhtService.GetClosestNodesAsync(new NodeId(target), cancellationToken).ConfigureAwait(false);

                var outResp = new Contracts.FindNodeResponse();
                foreach (var node in closerNodes)
                {
                    outResp.CloserPeers.Add(new Contracts.NodeInfo
                    {
                        PeerId = ByteString.CopyFrom(node.Id.Value),
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
                var conn = await _peerConnectionRepository.GetByIdAsync(remotePeerId).ConfigureAwait(false);
                if (conn?.IdentitySigningKey is null)
                {
                    _logger.LogWarning("No signing key for remote peer {PeerId} to handle PingRequest", remotePeerId);
                    return null;
                }
                var nodeIdBytes = System.Security.Cryptography.SHA256.HashData(conn.IdentitySigningKey.Value);
                await _mediator.Send(new Percolator.Dht.Messages.PingRequest(new NodeId(nodeIdBytes), conn.GrpcEndPoints.First().EndPoint), cancellationToken).ConfigureAwait(false);
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
                        SignedPreKeyId = new Guid(upload.SignedPreKeyId.ToByteArray()),
                        SignedPreKey = upload.SignedPreKey.ToByteArray(),
                        PreKeySignature = upload.PreKeySignature.ToByteArray(),
                        OneTimePreKeys = upload.OneTimePreKeys.Select(x => new SubmitPreKeyBundleCommand.OneTimePreKey(new Guid(x.Id.ToByteArray()), x.PublicKey.ToByteArray())).ToList(),
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
                    var bundle = await _mediator.Send(new GetPreKeyBundleQuery
                    {
                        TargetPublicSigningKeyHash = getReq.PublicKeyHash.ToByteArray()
                    }, cancellationToken).ConfigureAwait(false);
                    var resp = new GetPreKeyBundleResponse { Version = 1 };
                    if (bundle is not null)
                    {
                        var msg = new GetPreKeyBundleResponse.Types.PreKeyBundle
                        {
                            Version = 1,
                            IdentityKey = Google.Protobuf.ByteString.CopyFrom(bundle.IdentitySigningKey.Value),
                            SignedPreKeyId = Google.Protobuf.ByteString.CopyFrom(bundle.SignedPreKeyId.ToByteArray()),
                            SignedPreKey = Google.Protobuf.ByteString.CopyFrom(bundle.SignedPreKey.Value),
                            PreKeySignature = Google.Protobuf.ByteString.CopyFrom(bundle.SignedPreKeySignature.Value)
                        };
                        if (bundle.OneTimePreKey is not null)
                        {
                            msg.OneTimeKeyId = Google.Protobuf.ByteString.CopyFrom(bundle.OneTimePreKeyId!.Value.ToByteArray());
                            msg.OneTimeKey = Google.Protobuf.ByteString.CopyFrom(bundle.OneTimePreKey.Value);
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
            await _mediator.Send(new ProcessRelayedOpaquePayloadCommand(new Payload(relay.OpaquePayload.ToByteArray()), relayPeerId)).ConfigureAwait(false);
            return null;
        }

        // Chat handling
        if (env.ApplicationPayloadCase == InternalEnvelope.ApplicationPayloadOneofCase.ChatEnvelope)
        {
            var chat = env.ChatEnvelope;
            switch (chat.MessageCase)
            {
                case ChatEnvelope.MessageOneofCase.CreateGroup:
                {
                    var cg = chat.CreateGroup;
                    if (cg == null || !cg.HasGroupConversationGuid || cg.GroupConversationGuid.Length != 16)
                        throw new InvalidOperationException("CreateGroup.group_conversation_guid must be 16 bytes (GUID).");
                    var groupGuid = new Guid(cg.GroupConversationGuid.ToByteArray());

                    if (!cg.HasCreatorIdentityKey || cg.CreatorIdentityKey == null || cg.CreatorIdentityKey.Length == 0)
                        throw new InvalidOperationException("CreateGroup.creator_identity_key is required and must be non-empty.");

                    _logger.LogInformation("[CreateGroup] Received on SelfIdentityId={SelfIdentityId} GroupGuid={GroupGuid} Name='{Name}' Keys={Count} HasCreatorKey={HasCreator}", request.Context.SelfIdentityId, groupGuid, cg.HasName ? cg.Name : null, cg.InitialParticipantIdentityKeys.Count, cg.HasCreatorIdentityKey);

                    var spkis = new List<byte[]>();
                    foreach (var bs in cg.InitialParticipantIdentityKeys)
                    {
                        if (bs == null || bs.Length == 0) continue;
                        spkis.Add(bs.ToByteArray());
                    }
                    await _mediator.Send(new CreateGroupFromIdentityKeysCommand(
                        request.Context.SelfIdentityId,
                        groupGuid,
                        spkis,
                        cg.HasName ? cg.Name : null,
                        cg.CreatorIdentityKey.ToByteArray()
                    ), cancellationToken).ConfigureAwait(false);
                    return null;
                }
                case ChatEnvelope.MessageOneofCase.TextMessage:
                {
                    var text = chat.TextMessage;
                    if (text.MessageId == null || text.MessageId.Length != 16)
                        throw new InvalidOperationException("TextMessage.message_id must be 16 bytes (GUID).");

                    Guid? groupGuid = null;
                    if (text.HasGroupConversationGuid)
                    {
                        if (text.GroupConversationGuid.Length != 16)
                            throw new InvalidOperationException("TextMessage.group_conversation_guid must be 16 bytes (GUID).");
                        groupGuid = new Guid(text.GroupConversationGuid.ToByteArray());
                    }
                    byte[]? pkh = null;
                    if (text.HasPublicKeyHash)
                    {
                        if (text.PublicKeyHash.Length != 32)
                            throw new InvalidOperationException("TextMessage.public_key_hash must be 32 bytes (SHA-256).");
                        pkh = text.PublicKeyHash.ToByteArray();
                    }
                    if (groupGuid.HasValue && pkh is not null)
                        throw new InvalidOperationException("TextMessage must not set both group_conversation_guid and public_key_hash.");
                    ConversationLookupKey lookup = groupGuid.HasValue
                        ? ConversationLookupKey.ForGroup(groupGuid.Value)
                        : (pkh is not null && pkh.Length > 0)
                            ? ConversationLookupKey.ForPublicKeyHash(Pkh.FromBytes(pkh))
                            : ConversationLookupKey.ForDirectSession(request.Context.SessionId ?? throw new InvalidOperationException("SessionId required when no routing hint provided."));

                    var messageId = new MessageId(new Guid(text.MessageId.ToByteArray()));
                    var sentTs = text.SentTimestampUtc.ToDateTimeOffset();
                    await _mediator.Send(new PostTextMessageCommand(lookup, messageId, text.Content, sentTs), cancellationToken).ConfigureAwait(false);
                    return null;
                }
                case ChatEnvelope.MessageOneofCase.ReadReceipt:
                {
                    var rr = chat.ReadReceipt;
                    if (rr.MessageId == null || rr.MessageId.Length != 16)
                        throw new InvalidOperationException("ReadReceipt.message_id must be 16 bytes (GUID).");

                    Guid? groupGuid = null;
                    if (rr.HasGroupConversationGuid)
                    {
                        if (rr.GroupConversationGuid.Length != 16)
                            throw new InvalidOperationException("ReadReceipt.group_conversation_guid must be 16 bytes (GUID).");
                        groupGuid = new Guid(rr.GroupConversationGuid.ToByteArray());
                    }
                    byte[]? pkh = null;
                    if (rr.HasPublicKeyHash)
                    {
                        if (rr.PublicKeyHash.Length != 32)
                            throw new InvalidOperationException("ReadReceipt.public_key_hash must be 32 bytes (SHA-256).");
                        pkh = rr.PublicKeyHash.ToByteArray();
                    }
                    if (groupGuid.HasValue && pkh is not null)
                        throw new InvalidOperationException("ReadReceipt must not set both group_conversation_guid and public_key_hash.");
                    var lookup = groupGuid.HasValue
                        ? ConversationLookupKey.ForGroup(groupGuid.Value)
                        : (pkh is not null && pkh.Length > 0)
                            ? ConversationLookupKey.ForPublicKeyHash(Pkh.FromBytes(pkh))
                            : ConversationLookupKey.ForDirectSession(request.Context.SessionId ?? throw new InvalidOperationException("SessionId required when no routing hint provided."));

                    var messageId = new MessageId(new Guid(rr.MessageId.ToByteArray()));
                    var ts = rr.SentTimestampUtc.ToDateTimeOffset();
                    await _mediator.Send(new PostReadReceiptCommand(lookup, messageId, ts), cancellationToken).ConfigureAwait(false);
                    return null;
                }
                case ChatEnvelope.MessageOneofCase.EmojiAnnotation:
                {
                    var em = chat.EmojiAnnotation;
                    if (em.MessageId == null || em.MessageId.Length != 16)
                        throw new InvalidOperationException("EmojiAnnotation.message_id must be 16 bytes (GUID).");
                    if (string.IsNullOrWhiteSpace(em.Emoji))
                        throw new InvalidOperationException("EmojiAnnotation.emoji is required.");

                    Guid? groupGuid = null;
                    if (em.HasGroupConversationGuid)
                    {
                        if (em.GroupConversationGuid.Length != 16)
                            throw new InvalidOperationException("EmojiAnnotation.group_conversation_guid must be 16 bytes (GUID).");
                        groupGuid = new Guid(em.GroupConversationGuid.ToByteArray());
                    }
                    byte[]? pkh = null;
                    if (em.HasPublicKeyHash)
                    {
                        if (em.PublicKeyHash.Length != 32)
                            throw new InvalidOperationException("EmojiAnnotation.public_key_hash must be 32 bytes (SHA-256).");
                        pkh = em.PublicKeyHash.ToByteArray();
                    }
                    if (groupGuid.HasValue && pkh is not null)
                        throw new InvalidOperationException("EmojiAnnotation must not set both group_conversation_guid and public_key_hash.");
                    var lookup = groupGuid.HasValue
                        ? ConversationLookupKey.ForGroup(groupGuid.Value)
                        : (pkh is not null && pkh.Length > 0)
                            ? ConversationLookupKey.ForPublicKeyHash(Pkh.FromBytes(pkh))
                            : ConversationLookupKey.ForDirectSession(request.Context.SessionId ?? throw new InvalidOperationException("SessionId required when no routing hint provided."));

                    var messageId = new MessageId(new Guid(em.MessageId.ToByteArray()));
                    var ts = em.SentTimestampUtc.ToDateTimeOffset();
                    await _mediator.Send(new PostEmojiAnnotationCommand(lookup, messageId, em.Emoji, ts), cancellationToken).ConfigureAwait(false);
                    return null;
                }
                case ChatEnvelope.MessageOneofCase.DeliveredReceipt:
                {
                    var dr = chat.DeliveredReceipt;
                    if (dr.MessageId == null || dr.MessageId.Length != 16)
                        throw new InvalidOperationException("DeliveredReceipt.message_id must be 16 bytes (GUID).");

                    Guid? groupGuid = null;
                    if (dr.HasGroupConversationGuid)
                    {
                        if (dr.GroupConversationGuid.Length != 16)
                            throw new InvalidOperationException("DeliveredReceipt.group_conversation_guid must be 16 bytes (GUID).");
                        groupGuid = new Guid(dr.GroupConversationGuid.ToByteArray());
                    }
                    byte[]? pkh = null;
                    if (dr.HasPublicKeyHash)
                    {
                        if (dr.PublicKeyHash.Length != 32)
                            throw new InvalidOperationException("DeliveredReceipt.public_key_hash must be 32 bytes (SHA-256).");
                        pkh = dr.PublicKeyHash.ToByteArray();
                    }
                    if (groupGuid.HasValue && pkh is not null)
                        throw new InvalidOperationException("DeliveredReceipt must not set both group_conversation_guid and public_key_hash.");
                    var lookup = groupGuid.HasValue
                        ? ConversationLookupKey.ForGroup(groupGuid.Value)
                        : (pkh is not null && pkh.Length > 0)
                            ? ConversationLookupKey.ForPublicKeyHash(Pkh.FromBytes(pkh))
                            : ConversationLookupKey.ForDirectSession(request.Context.SessionId ?? throw new InvalidOperationException("SessionId required when no routing hint provided."));

                    var messageId = new MessageId(new Guid(dr.MessageId.ToByteArray()));
                    var ts = dr.SentTimestampUtc.ToDateTimeOffset();
                    await _mediator.Send(new PostDeliveredReceiptCommand(lookup, messageId, ts), cancellationToken).ConfigureAwait(false);
                    return null;
                }
                case ChatEnvelope.MessageOneofCase.SignedAdminOperation:
                {
                    var sao = chat.SignedAdminOperation;
                    if (sao == null || sao.Payload == null || !sao.HasSignature)
                        throw new InvalidOperationException("SignedAdminOperation.payload and signature are required.");
                    if (!sao.Payload.HasGroupConversationGuid || sao.Payload.GroupConversationGuid.Length != 16)
                        throw new InvalidOperationException("SignedAdminOperation.payload.group_conversation_guid must be 16 bytes (GUID).");
                    if (!sao.Payload.HasOpId || sao.Payload.OpId.Length != 16)
                        throw new InvalidOperationException("SignedAdminOperation.payload.op_id must be 16 bytes (GUID).");

                    var groupGuid = new Guid(sao.Payload.GroupConversationGuid.ToByteArray());
                    var lookup = ConversationLookupKey.ForGroup(groupGuid);

                    var opId = new Guid(sao.Payload.OpId.ToByteArray());
                    var sentUtc = sao.Payload.SentTimestampUtc.ToDateTimeOffset();
                    ulong? adminSeq = sao.Payload.HasAdminSequenceNumber ? sao.Payload.AdminSequenceNumber : null;

                    AdminPublicKey? grantee = null;
                    List<Percolator.Chat.ValueObjects.ParticipantId>? add = null;
                    List<Percolator.Chat.ValueObjects.ParticipantId>? remove = null;
                    bool? leave = null;
                    string? newName2 = null;
                    GroupAvatar? newAvatar2 = null;

                    switch (sao.Payload.OperationCase)
                    {
                        case AdminOperationPayload.OperationOneofCase.GrantAdmin:
                            if (!sao.Payload.GrantAdmin.HasGranteePublicKey)
                                throw new InvalidOperationException("GrantAdmin.grantee_public_key is required.");
                            grantee = new AdminPublicKey(sao.Payload.GrantAdmin.GranteePublicKey.ToByteArray());
                            break;
                        case AdminOperationPayload.OperationOneofCase.RevokeAdmin:
                            if (!sao.Payload.RevokeAdmin.HasGranteePublicKey)
                                throw new InvalidOperationException("RevokeAdmin.grantee_public_key is required.");
                            grantee = new AdminPublicKey(sao.Payload.RevokeAdmin.GranteePublicKey.ToByteArray());
                            break;
                        case AdminOperationPayload.OperationOneofCase.UpdateGroupMembership:
                            add = new List<Percolator.Chat.ValueObjects.ParticipantId>();
                            foreach (var b in sao.Payload.UpdateGroupMembership.MembersToAdd)
                            {
                                if (b.Length != 16) throw new InvalidOperationException("members_to_add must be GUID bytes (16).");
                                add.Add(new Percolator.Chat.ValueObjects.ParticipantId(new Guid(b.ToByteArray())));
                            }
                            remove = new List<Percolator.Chat.ValueObjects.ParticipantId>();
                            foreach (var b in sao.Payload.UpdateGroupMembership.MembersToRemove)
                            {
                                if (b.Length != 16) throw new InvalidOperationException("members_to_remove must be GUID bytes (16).");
                                remove.Add(new Percolator.Chat.ValueObjects.ParticipantId(new Guid(b.ToByteArray())));
                            }
                            leave = sao.Payload.UpdateGroupMembership.HasLeaveGroup ? sao.Payload.UpdateGroupMembership.LeaveGroup : (bool?)null;
                            break;
                        case AdminOperationPayload.OperationOneofCase.UpdateGroupInfo:
                            newName2 = sao.Payload.UpdateGroupInfo.HasNewGroupName ? sao.Payload.UpdateGroupInfo.NewGroupName : null;
                            if (sao.Payload.UpdateGroupInfo.HasNewGroupAvatar)
                            {
                                newAvatar2 = new GroupAvatar(sao.Payload.UpdateGroupInfo.NewGroupAvatar.ToByteArray());
                            }
                            break;
                        default:
                            throw new InvalidOperationException($"Unsupported admin operation variant: {sao.Payload.OperationCase}");
                    }

                    var signatureBytes = sao.Signature.ToByteArray();
                    var payloadBytes = CanonicalPayload.ForAdminOperation(sao.Payload);
                    // Dispatch to domain ops based on actual payload operation
                    var opCase = sao.Payload.OperationCase;
                    if (opCase == AdminOperationPayload.OperationOneofCase.GrantAdmin)
                    {
                        if (grantee is null) throw new InvalidOperationException("GrantAdmin requires grantee");
                        await _adminOps.GrantAdminAsync(lookup, opId, sentUtc, grantee.Value, signatureBytes, payloadBytes, cancellationToken).ConfigureAwait(false);
                    }
                    else if (opCase == AdminOperationPayload.OperationOneofCase.RevokeAdmin)
                    {
                        if (grantee is null) throw new InvalidOperationException("RevokeAdmin requires grantee");
                        await _adminOps.RevokeAdminAsync(lookup, opId, sentUtc, grantee.Value, signatureBytes, payloadBytes, cancellationToken).ConfigureAwait(false);
                    }
                    else if (opCase == AdminOperationPayload.OperationOneofCase.UpdateGroupMembership)
                    {
                        await _adminOps.UpdateGroupMembershipAsync(lookup, opId, sentUtc, add, remove, leave, signatureBytes, payloadBytes, cancellationToken).ConfigureAwait(false);
                    }
                    else if (opCase == AdminOperationPayload.OperationOneofCase.UpdateGroupInfo)
                    {
                        await _adminOps.UpdateGroupInfoAsync(lookup, opId, sentUtc, newName2, null, signatureBytes, payloadBytes, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Unsupported admin operation variant: {opCase}");
                    }
                    return null;
                }
                case ChatEnvelope.MessageOneofCase.KeyAdoptionConfirmation:
                {
                    var kac = chat.KeyAdoptionConfirmation;
                    if (kac == null || !kac.HasGroupConversationGuid || kac.GroupConversationGuid.Length != 16)
                        throw new InvalidOperationException("SignedKeyAdoptionConfirmation.group_conversation_guid must be 16 bytes (GUID).");
                    if (!kac.HasKeyVersion)
                        throw new InvalidOperationException("SignedKeyAdoptionConfirmation.key_version is required.");
                    if (!kac.HasAdopterIdentityKey || kac.AdopterIdentityKey.Length == 0)
                        throw new InvalidOperationException("SignedKeyAdoptionConfirmation.adopter_identity_key is required.");
                    if (!kac.HasSignature || kac.Signature.Length == 0)
                        throw new InvalidOperationException("SignedKeyAdoptionConfirmation.signature is required.");

                    var groupGuid = new Guid(kac.GroupConversationGuid.ToByteArray());
                    var lookup = ConversationLookupKey.ForGroup(groupGuid);
                    await _mediator.Send(new ReceiveKeyAdoptionConfirmationCommand(
                        lookup,
                        new GroupKeyVersion(kac.KeyVersion),
                        new Percolator.Chat.ValueObjects.IdentityPublicKey(kac.AdopterIdentityKey.ToByteArray()),
                        kac.SentTimestampUtc.ToDateTimeOffset(),
                        kac.Signature.ToByteArray()
                    ), cancellationToken).ConfigureAwait(false);
                    return null;
                }
                case ChatEnvelope.MessageOneofCase.AdminCommitOperation:
                {
                    var aco = chat.AdminCommitOperation;
                    if (aco == null || !aco.HasGroupConversationGuid || aco.GroupConversationGuid.Length != 16)
                        throw new InvalidOperationException("SignedAdminCommitOperation.group_conversation_guid must be 16 bytes (GUID).");
                    if (!aco.HasOpId || aco.OpId.Length != 16)
                        throw new InvalidOperationException("SignedAdminCommitOperation.op_id must be 16 bytes (GUID).");
                    if (!aco.HasCommittedKeyVersion)
                        throw new InvalidOperationException("SignedAdminCommitOperation.committed_key_version is required.");
                    if (!aco.HasSignature || aco.Signature.Length == 0)
                        throw new InvalidOperationException("SignedAdminCommitOperation.signature is required.");
                    if (!aco.HasAdminSequenceNumber)
                        throw new InvalidOperationException("SignedAdminCommitOperation.admin_sequence_number is required.");

                    var groupGuid = new Guid(aco.GroupConversationGuid.ToByteArray());
                    var lookup = ConversationLookupKey.ForGroup(groupGuid);
                    await _mediator.Send(new ReceiveAdminCommitCommand(
                        lookup,
                        new Guid(aco.OpId.ToByteArray()),
                        new GroupKeyVersion(aco.CommittedKeyVersion),
                        aco.SentTimestampUtc.ToDateTimeOffset(),
                        aco.AdminSequenceNumber,
                        aco.Signature.ToByteArray()
                    ), cancellationToken).ConfigureAwait(false);
                    return null;
                }
                case ChatEnvelope.MessageOneofCase.KeyDistribution:
                {
                    var kd = chat.KeyDistribution;
                    if (kd == null || !kd.HasGroupConversationGuid || kd.GroupConversationGuid.Length != 16)
                        throw new InvalidOperationException("KeyDistributionPayload.group_conversation_guid must be 16 bytes (GUID).");
                    if (!kd.HasKeyVersion)
                        throw new InvalidOperationException("KeyDistributionPayload.key_version is required.");
                    if (!kd.HasEncryptedGroupKeyForRecipient || kd.EncryptedGroupKeyForRecipient.Length == 0)
                        throw new InvalidOperationException("KeyDistributionPayload.encrypted_group_key_for_recipient is required.");

                    var groupGuid = new Guid(kd.GroupConversationGuid.ToByteArray());
                    var lookup = ConversationLookupKey.ForGroup(groupGuid);
                    await _mediator.Send(new ReceiveKeyDistributionCommand(
                        lookup,
                        new GroupKeyVersion(kd.KeyVersion),
                        new EncryptedGroupKey(kd.EncryptedGroupKeyForRecipient.ToByteArray())
                    ), cancellationToken).ConfigureAwait(false);
                    return null;
                }
                case ChatEnvelope.MessageOneofCase.UpdateGroupMembershipRequest:
                {
                    var ugr = chat.UpdateGroupMembershipRequest;
                    if (!ugr.HasGroupConversationGuid || ugr.GroupConversationGuid.Length != 16)
                    {
                        throw new InvalidOperationException("UpdateGroupMembershipRequest.group_conversation_guid must be 16 bytes (GUID).");
                    }
                    var groupGuid = new Guid(ugr.GroupConversationGuid.ToByteArray());
                    var lookup = ConversationLookupKey.ForGroup(groupGuid);

                    var toAdd = new List<Percolator.Chat.ValueObjects.ParticipantId>(ugr.MembersToAdd.Count);
                    foreach (var b in ugr.MembersToAdd)
                    {
                        if (b.Length != 16) throw new InvalidOperationException("members_to_add must be GUID bytes (16).");
                        toAdd.Add(new Percolator.Chat.ValueObjects.ParticipantId(new Guid(b.ToByteArray())));
                    }
                    var toRemove = new List<Percolator.Chat.ValueObjects.ParticipantId>(ugr.MembersToRemove.Count);
                    foreach (var b in ugr.MembersToRemove)
                    {
                        if (b.Length != 16) throw new InvalidOperationException("members_to_remove must be GUID bytes (16).");
                        toRemove.Add(new Percolator.Chat.ValueObjects.ParticipantId(new Guid(b.ToByteArray())));
                    }

                    await _mediator.Send(new UpdateGroupMembershipCommand(lookup, toAdd, toRemove, ugr.LeaveGroup), cancellationToken).ConfigureAwait(false);
                    return null;
                }
                case ChatEnvelope.MessageOneofCase.UpdateGroupInfoRequest:
                {
                    var ugi = chat.UpdateGroupInfoRequest;
                    if (!ugi.HasGroupConversationGuid || ugi.GroupConversationGuid.Length != 16)
                    {
                        throw new InvalidOperationException("UpdateGroupInfoRequest.group_conversation_guid must be 16 bytes (GUID).");
                    }
                    var groupGuid = new Guid(ugi.GroupConversationGuid.ToByteArray());
                    var lookup = ConversationLookupKey.ForGroup(groupGuid);
                    var newName = ugi.HasNewGroupName ? ugi.NewGroupName : null;
                    await _mediator.Send(new UpdateGroupInfoCommand(lookup, newName), cancellationToken).ConfigureAwait(false);
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