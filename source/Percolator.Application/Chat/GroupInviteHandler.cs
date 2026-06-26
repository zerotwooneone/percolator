using Microsoft.Extensions.Logging;
using Percolator.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
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
    private readonly IPeerIdentityQueries _peerIdentityQueries;
    private readonly ILogger<GroupInviteHandler> _logger;

    public GroupInviteHandler(
        IGroupCryptoStateRepository groupCryptoStateRepository,
        IGroupConversationRepository groupConversationRepository,
        ISelfIdentityQueries selfIdentityQueries,
        ISenderKeyCryptographyService senderKeyCryptographyService,
        IGroupCryptographyService groupCryptographyService,
        IPeerIdentityQueries peerIdentityQueries,
        ILogger<GroupInviteHandler> logger)
    {
        _groupCryptoStateRepository = groupCryptoStateRepository;
        _groupConversationRepository = groupConversationRepository;
        _selfIdentityQueries = selfIdentityQueries;
        _senderKeyCryptographyService = senderKeyCryptographyService;
        _groupCryptographyService = groupCryptographyService;
        _peerIdentityQueries = peerIdentityQueries;
        _logger = logger;
    }

    public async Task HandleGroupInviteAsync(GroupInvite invite, SelfId selfIdentityId, Percolator.Identity.DeviceId sourceDeviceId, CancellationToken ct = default)
    {
        // Validate required fields
        if (invite.ConversationId is null || invite.ConversationId.IsEmpty)
            throw new ArgumentException("conversation_id is required.");
        if (invite.InviterPkh is null || invite.InviterPkh.IsEmpty)
            throw new ArgumentException("inviter_pkh is required.");
        if (invite.GroupMasterKey is null || invite.GroupMasterKey.IsEmpty)
            throw new ArgumentException("group_master_key is required.");
        if (invite.SenderKeyDistribution is null || invite.SenderKeyDistribution.IsEmpty)
            throw new ArgumentException("sender_key_distribution is required.");
        if (invite.RelayPkh is null || invite.RelayPkh.IsEmpty)
            throw new ArgumentException("relay_pkh is required.");

        // Step 1: Parse ConversationId from invite
        var conversationId = new Percolator.Chat.Messaging.ValueObjects.ConversationId(new Guid(invite.ConversationId.ToByteArray()));

        // Step 2: Parse inviterPkh from invite
        var inviterPkh = Pkh.FromBytesOwned(invite.InviterPkh.ToByteArray());

        // Step 3: Parse groupMasterKey from invite
        var groupMasterKeyBytes = GroupMasterKeyBytes.FromBytesOwned(invite.GroupMasterKey.ToByteArray());

        // Step 4: Parse senderKeyDistributionMessage from invite
        var distributionBytes = ChatSenderKeyDistributionMessageBytes.FromBytesOwned(invite.SenderKeyDistribution.ToByteArray());

        // Step 5: Parse relayPkh from invite
        var relayPkh = Pkh.FromBytesOwned(invite.RelayPkh.ToByteArray());

        // Step 6: Persist master key
        await _groupCryptoStateRepository.UpsertGroupMasterKeyAsync(conversationId, groupMasterKeyBytes, ct).ConfigureAwait(false);

        // Step 7: Derive group public params from master key (Signal Group V2 deterministic derivation)
        var cryptoGroupMasterKey = _groupCryptographyService.DeserializeGroupMasterKey(groupMasterKeyBytes.Span);
        var cryptoPublicParams = _groupCryptographyService.DeriveGroupPublicParams(cryptoGroupMasterKey);
        var chatPublicParams = RelayGroupPublicParamsBytes.FromSpan(cryptoPublicParams.Span);

        // Step 8: Process sender key distribution message
        // Convert chat domain types to cryptography domain types
        var cryptoConversationId = new Percolator.Cryptography.Primitives.ConversationId(conversationId.Value);
        
        // Lookup inviter's PeerId by PKH
        var inviterPeerId = await _peerIdentityQueries.GetPeerIdByPkhAsync(IdentityPublicKeyHash.FromSpan(inviterPkh.Span), ct).ConfigureAwait(false);
        if (inviterPeerId is null)
        {
            throw new InvalidOperationException($"Could not resolve inviter identity for PKH {inviterPkh}");
        }
        var senderPeerId = new Percolator.Cryptography.Primitives.PeerId(inviterPeerId.Value.Value);
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

        var relayIdentity = new GroupParticipantId(relayPkh, null);

        // Fetch the local identity's actual PKH and bridged PeerId
        var selfInfo = await _selfIdentityQueries.GetIdentityParticipantInfoAsync(selfIdentityId, ct).ConfigureAwait(false);
        if (selfInfo is null)
        {
            throw new InvalidOperationException($"Could not resolve self identity info for ID {selfIdentityId}");
        }

        var selfParticipantId = new GroupParticipantId(selfInfo.Value.Pkh, new ChatPeerId(selfInfo.Value.PeerId));
        var selfMember = new GroupMember(
            conversationId,
            selfParticipantId,
            GroupMemberRole.Member,
            DateTimeOffset.UtcNow);

        var inviterParticipantId = new GroupParticipantId(inviterPkh, null);
        var inviterMember = new GroupMember(
            conversationId,
            inviterParticipantId,
            GroupMemberRole.Admin,
            DateTimeOffset.UtcNow);

        var groupConversation = new GroupConversation(
            conversationId,
            groupState,
            relayIdentity,
            new[] { selfMember, inviterMember },
            invite.Name);

        // Step 9: Save via repository
        await _groupConversationRepository.AddAsync(groupConversation, selfIdentityId.Value, ct).ConfigureAwait(false);

        _logger.LogInformation("Group invite processed successfully for conversation {ConversationId}", conversationId);
    }
}
