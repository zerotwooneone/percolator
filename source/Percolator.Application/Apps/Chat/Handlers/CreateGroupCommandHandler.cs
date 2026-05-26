using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Network;
using Percolator.Chat;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;

namespace Percolator.Application.Apps.Chat;

public sealed class CreateGroupCommandHandler : IRequestHandler<CreateGroupCommand, Guid>
{
    private readonly IDirectSessionRepository _directSessionRepository;
    private readonly ISelfIdentityRepository _selfIdentityRepository;
    private readonly IGroupCryptographyService _groupCryptoService;
    private readonly IGroupCryptoStateRepository _groupCryptoStateRepository;
    private readonly IGroupConversationRepository _groupConversationRepository;
    private readonly IRemoteEnvelopeSender _envelopeSender;
    private readonly IPeerPublicSigningKeyStore _keyStore;
    private readonly ILogger<CreateGroupCommandHandler> _logger;

    public CreateGroupCommandHandler(
        IDirectSessionRepository directSessionRepository,
        ISelfIdentityRepository selfIdentityRepository,
        IGroupCryptographyService groupCryptoService,
        IGroupCryptoStateRepository groupCryptoStateRepository,
        IGroupConversationRepository groupConversationRepository,
        IRemoteEnvelopeSender envelopeSender,
        IPeerPublicSigningKeyStore keyStore,
        ILogger<CreateGroupCommandHandler> logger)
    {
        _directSessionRepository = directSessionRepository;
        _selfIdentityRepository = selfIdentityRepository;
        _groupCryptoService = groupCryptoService;
        _groupCryptoStateRepository = groupCryptoStateRepository;
        _groupConversationRepository = groupConversationRepository;
        _envelopeSender = envelopeSender;
        _keyStore = keyStore;
        _logger = logger;
    }

    public async Task<Guid> Handle(CreateGroupCommand request, CancellationToken cancellationToken)
    {
        // Load self identity to get creator's identity key
        var selfIdentity = await _selfIdentityRepository.GetByIdAsync(new SelfId(request.SelfIdentityId), cancellationToken)
            ?? throw new InvalidOperationException($"SelfIdentity not found for id {request.SelfIdentityId}.");

        var creatorIdentityKey = selfIdentity.GetActiveKey(DateTimeOffset.UtcNow)
            ?? throw new InvalidOperationException($"No active identity key found for self identity {request.SelfIdentityId}.");

        // Validate that secure sessions exist with all initial members
        foreach (var memberPeerId in request.InitialMembers)
        {
            var networkPeerId = new Percolator.Network.PeerId(memberPeerId.Value);
            var session = await _directSessionRepository.GetByRemotePeerIdAsync(networkPeerId, request.SelfIdentityId);
            if (session is null)
            {
                throw new InvalidOperationException($"No secure session exists with peer {memberPeerId.Value}. Cannot add to group.");
            }
        }

        // Generate new ConversationId
        var conversationId = Guid.NewGuid();

        // Generate new GroupMasterKey (32 random bytes)
        var randomness = new byte[32];
        Random.Shared.NextBytes(randomness);
        var groupMasterKey = _groupCryptoService.GenerateGroupMasterKey(randomness);

        // Derive GroupId from master key
        var groupId = _groupCryptoService.DeriveGroupId(groupMasterKey);

        // Persist GroupMasterKey
        await _groupCryptoStateRepository.UpsertGroupMasterKeyAsync(
            new ConversationId(conversationId),
            groupMasterKey,
            cancellationToken);

        // Create CreateGroup envelope
        var createGroupEnvelope = new ChatEnvelope
        {
            CreateGroup = new CreateGroup
            {
                ConversationId = ByteString.CopyFrom(conversationId.ToByteArray()),
                CreatorIdentityKey = ByteString.CopyFrom(creatorIdentityKey.Spki),
                Name = request.GroupName
            }
        };

        // Add initial participant identity keys (including creator)
        foreach (var memberPeerId in request.InitialMembers)
        {
            var memberKeyHash = await _keyStore.GetPublicKeyHashByPeerIdAsync(memberPeerId, cancellationToken);
            if (memberKeyHash is null)
            {
                _logger.LogWarning("No public key hash found for peer {PeerId}, skipping from initial participants", memberPeerId);
                continue;
            }

            // For now, we'll use the peer's public key hash to look up their identity key
            // In a real implementation, we'd need to resolve the actual SPKI bytes
            // For simplicity, we'll add the creator's key as a placeholder
            createGroupEnvelope.CreateGroup.InitialParticipantIdentityKeys.Add(ByteString.CopyFrom(creatorIdentityKey.Spki));
        }

        // Send CreateGroup to each initial member
        foreach (var memberPeerId in request.InitialMembers)
        {
            var publicKeyHash = await _keyStore.GetPublicKeyHashByPeerIdAsync(memberPeerId, cancellationToken);
            var route = new RecipientRoute(memberPeerId, publicKeyHash);
            await _envelopeSender.SendChatEnvelopeToPeerAsync(createGroupEnvelope, route, cancellationToken);
        }

        // Create GroupKeyBootstrap envelope
        var bootstrapEnvelope = new ChatEnvelope
        {
            GroupKeyBootstrap = new GroupKeyBootstrap
            {
                ConversationId = ByteString.CopyFrom(conversationId.ToByteArray()),
                GroupMasterKeyBytes = ByteString.CopyFrom(groupMasterKey.ToArray())
            }
        };

        // Send GroupKeyBootstrap to each initial member
        foreach (var memberPeerId in request.InitialMembers)
        {
            var publicKeyHash = await _keyStore.GetPublicKeyHashByPeerIdAsync(memberPeerId, cancellationToken);
            var route = new RecipientRoute(memberPeerId, publicKeyHash);
            await _envelopeSender.SendChatEnvelopeToPeerAsync(bootstrapEnvelope, route, cancellationToken);
        }

        // Create local GroupConversation with GroupState and GroupMembers
        var now = DateTimeOffset.UtcNow;
        var groupState = new GroupState(
            new ConversationId(conversationId),
            0,
            request.GroupName,
            now,
            now
        );

        var groupMembers = new List<GroupMember>();
        foreach (var memberPeerId in request.InitialMembers)
        {
            var role = memberPeerId == selfIdentity.PeerId
                ? GroupMemberRole.Admin
                : GroupMemberRole.Member;

            var groupMember = new GroupMember(
                new ConversationId(conversationId),
                memberPeerId,
                role,
                now
            );
            groupMembers.Add(groupMember);
        }

        var groupConversation = new GroupConversation(
            new ConversationId(conversationId),
            groupState,
            groupMembers,
            request.GroupName
        );
        await _groupConversationRepository.AddAsync(groupConversation, request.SelfIdentityId, cancellationToken);

        _logger.LogInformation("Created group {ConversationId} with {MemberCount} members", conversationId, request.InitialMembers.Count);

        return conversationId;
    }
}
