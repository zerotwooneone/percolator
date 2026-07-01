using Microsoft.Extensions.Logging;
using Percolator.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using ChatPeerId = Percolator.Chat.GroupMembership.ChatPeerId;
using DeviceId = Percolator.Cryptography.Primitives.DeviceId;

namespace Percolator.Application.Chat;

/// <summary>
/// Handler for processing incoming group invitations.
/// </summary>
public sealed class GroupInviteHandler : IGroupInviteHandler
{
    private readonly IGroupCryptoStateRepository _groupCryptoStateRepository;
    private readonly IGroupConversationRepository _groupConversationRepository;
    private readonly ISelfIdentityQueries _selfIdentityQueries;
    private readonly ISenderKeyCryptographyService _senderKeyCryptographyService;
    private readonly IGroupCryptographyService _groupCryptographyService;
    private readonly IPeerIdentityRepository _peerIdentityRepository;
    private readonly IPeerRoutingProfileRepository _peerRoutingProfileRepository;
    private readonly ILogger<GroupInviteHandler> _logger;

    public GroupInviteHandler(
        IGroupCryptoStateRepository groupCryptoStateRepository,
        IGroupConversationRepository groupConversationRepository,
        ISelfIdentityQueries selfIdentityQueries,
        ISenderKeyCryptographyService senderKeyCryptographyService,
        IGroupCryptographyService groupCryptographyService,
        IPeerIdentityRepository peerIdentityRepository,
        IPeerRoutingProfileRepository peerRoutingProfileRepository,
        ILogger<GroupInviteHandler> logger)
    {
        _groupCryptoStateRepository = groupCryptoStateRepository;
        _groupConversationRepository = groupConversationRepository;
        _selfIdentityQueries = selfIdentityQueries;
        _senderKeyCryptographyService = senderKeyCryptographyService;
        _groupCryptographyService = groupCryptographyService;
        _peerIdentityRepository = peerIdentityRepository;
        _peerRoutingProfileRepository = peerRoutingProfileRepository;
        _logger = logger;
    }

    public async Task HandleGroupInviteAsync(GroupInvite invite, SelfId selfIdentityId, Percolator.Identity.DeviceId sourceDeviceId, CancellationToken ct = default)
    {
        // Validate required fields
        if (invite.ConversationId is null || invite.ConversationId.IsEmpty)
            throw new ArgumentException("conversation_id is required.");
        if (invite.InviterPublicIdentityId is null || invite.InviterPublicIdentityId.IsEmpty)
            throw new ArgumentException("inviter_public_identity_id is required.");
        if (invite.GroupMasterKey is null || invite.GroupMasterKey.IsEmpty)
            throw new ArgumentException("group_master_key is required.");
        if (invite.SenderKeyDistribution is null || invite.SenderKeyDistribution.IsEmpty)
            throw new ArgumentException("sender_key_distribution is required.");
        if (invite.RelayPublicIdentityId is null || invite.RelayPublicIdentityId.IsEmpty)
            throw new ArgumentException("relay_public_identity_id is required.");
        if (string.IsNullOrEmpty(invite.RelayHost) || !invite.HasRelayPort)
            throw new ArgumentException("relay_host and relay_port are required.");

        // Step 1: Parse ConversationId from invite
        var conversationId = new Percolator.Chat.Messaging.ValueObjects.ConversationId(new Guid(invite.ConversationId.ToByteArray()));

        // Step 2: Parse inviterPublicIdentityId from invite
        var inviterPublicIdentityId = new Percolator.Identity.PublicIdentityId(new Guid(invite.InviterPublicIdentityId.ToByteArray()));

        // Step 3: Parse groupMasterKey from invite
        var groupMasterKeyBytes = GroupMasterKeyBytes.FromBytesOwned(invite.GroupMasterKey.ToByteArray());

        // Step 4: Parse senderKeyDistributionMessage from invite
        var distributionBytes = ChatSenderKeyDistributionMessageBytes.FromBytesOwned(invite.SenderKeyDistribution.ToByteArray());

        // Step 5: Parse relayPublicIdentityId from invite
        var relayPublicIdentityId = new Percolator.Identity.PublicIdentityId(new Guid(invite.RelayPublicIdentityId.ToByteArray()));

        // Step 5.5: Save relay endpoint if provided in the invite
        var relayIdentity = await _peerIdentityRepository.GetOrCreateAsync(relayPublicIdentityId, ct).ConfigureAwait(false);
        var relayPeerId = new Percolator.Network.PeerId(relayIdentity.Id.Value);
        var relayProfile = await _peerRoutingProfileRepository.GetByIdAsync(relayPeerId, ct).ConfigureAwait(false);
            
        var endpoint = new System.Net.DnsEndPoint(invite.RelayHost, invite.RelayPort);
        var grpcEndpoint = new GrpcEndPoint(endpoint, DateTimeOffset.UtcNow);
            
        if (relayProfile == null)
        {
            relayProfile = new Percolator.Network.PeerRoutingProfile();
            relayProfile.BindIdentity(relayPeerId);
            relayProfile.AddGrpcEndPoint(grpcEndpoint, DateTimeOffset.UtcNow);
            await _peerRoutingProfileRepository.UpsertAsync(relayProfile, ct).ConfigureAwait(false);
            _logger.LogInformation("Created new routing profile for relay {RelayPeerId} with endpoint {RelayHost}:{RelayPort}", 
                relayPeerId.Value, invite.RelayHost, invite.RelayPort);
        }
        else
        {
            relayProfile.AddGrpcEndPoint(grpcEndpoint, DateTimeOffset.UtcNow);
            await _peerRoutingProfileRepository.UpsertAsync(relayProfile, ct).ConfigureAwait(false);
            _logger.LogInformation("Updated routing profile for relay {RelayPeerId} with endpoint {RelayHost}:{RelayPort}", 
                relayPeerId.Value, invite.RelayHost, invite.RelayPort);
        }

        // Step 6: Persist master key
        await _groupCryptoStateRepository.UpsertGroupMasterKeyAsync(conversationId, groupMasterKeyBytes, ct).ConfigureAwait(false);

        // Step 7: Derive group public params from master key (Signal Group V2 deterministic derivation)
        var cryptoGroupMasterKey = _groupCryptographyService.DeserializeGroupMasterKey(groupMasterKeyBytes.Span);
        var cryptoPublicParams = _groupCryptographyService.DeriveGroupPublicParams(cryptoGroupMasterKey);
        var chatPublicParams = RelayGroupPublicParamsBytes.FromSpan(cryptoPublicParams.Span);

        // Step 8: Process sender key distribution message
        // Convert chat domain types to cryptography domain types
        var cryptoConversationId = new Percolator.Cryptography.Primitives.ConversationId(conversationId.Value);

        // Ensure inviter identity exists (get or create stub)
        var inviterIdentity = await _peerIdentityRepository.GetOrCreateAsync(inviterPublicIdentityId, ct).ConfigureAwait(false);
        var senderPeerId = new Percolator.Cryptography.Primitives.PeerId(inviterIdentity.Id.Value);
        var senderDeviceId = new DeviceId(sourceDeviceId.Value);
        var distributionMessage = SenderKeyDistributionMessageBytes.FromSpan(distributionBytes.Span);

        _senderKeyCryptographyService.ProcessSenderKeyDistributionMessage(
            cryptoConversationId,
            senderPeerId,
            senderDeviceId,
            distributionMessage);

        _logger.LogInformation("Processed sender key distribution for group {ConversationId} from device {DeviceId}", conversationId, sourceDeviceId);

        // Step 9: Initialize GroupConversation
        var groupState = new GroupState(
            conversationId,
            epoch: 0,
            name: invite.Name,
            publicParams: chatPublicParams,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        // Fetch the local identity's actual PKH and bridged PeerId
        var selfInfo = await _selfIdentityQueries.GetIdentityParticipantInfoAsync(selfIdentityId, ct).ConfigureAwait(false);
        if (selfInfo is null)
        {
            throw new InvalidOperationException($"Could not resolve self identity info for ID {selfIdentityId}");
        }

        var selfChatPublicId = new Percolator.Chat.GroupLedger.PublicIdentityId( selfInfo.Value.PublicIdentityId.Value);
        var chatSelfId = new ChatSelfId(selfIdentityId.Value);
        var selfMember = new GroupMember(
            conversationId,
            new LocalParticipantId(selfChatPublicId, chatSelfId),
            GroupMemberRole.Member,
            DateTimeOffset.UtcNow);

        var inviterPeerId = new ChatPeerId(inviterIdentity.Id.Value);
        var invitierPublicId = new Percolator.Chat.GroupLedger.PublicIdentityId(inviterIdentity.PublicIdentityId.Value);
        var inviterMember = new GroupMember(
            conversationId,
            new RemoteParticipantId(invitierPublicId, inviterPeerId),
            GroupMemberRole.Admin,
            DateTimeOffset.UtcNow);

        var groupConversation = new GroupConversation(
            conversationId,
            groupState,
            new ChatPeerId(relayPeerId.Value),
            new[] { selfMember, inviterMember },
            invite.Name);

        // Step 9: Save via repository
        await _groupConversationRepository.AddAsync(groupConversation, chatSelfId, ct).ConfigureAwait(false);

        _logger.LogInformation("Group invite processed successfully for conversation {ConversationId}", conversationId);
    }
}
