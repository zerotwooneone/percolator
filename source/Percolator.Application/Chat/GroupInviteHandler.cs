using Microsoft.Extensions.Logging;
using Percolator.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

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
    private readonly ILogger<GroupInviteHandler> _logger;

    public GroupInviteHandler(
        IGroupCryptoStateRepository groupCryptoStateRepository,
        IGroupConversationRepository groupConversationRepository,
        ISelfIdentityQueries selfIdentityQueries,
        ISenderKeyCryptographyService senderKeyCryptographyService,
        ILogger<GroupInviteHandler> logger)
    {
        _groupCryptoStateRepository = groupCryptoStateRepository;
        _groupConversationRepository = groupConversationRepository;
        _selfIdentityQueries = selfIdentityQueries;
        _senderKeyCryptographyService = senderKeyCryptographyService;
        _logger = logger;
    }

    public async Task HandleGroupInviteAsync(GroupInvite invite, int selfIdentityId, uint sourceDeviceId, CancellationToken ct = default)
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

        // Step 7: Process sender key distribution message
        // Convert chat domain types to cryptography domain types
        var cryptoConversationId = new Percolator.Cryptography.Primitives.ConversationId(conversationId.Value);
        var senderPeerId = new Percolator.Cryptography.Primitives.PeerId(inviterPkh.Span);
        var senderDeviceId = new DeviceId(sourceDeviceId);
        var distributionMessage = SenderKeyDistributionMessageBytes.FromSpan(distributionBytes.Span);

        _senderKeyCryptographyService.ProcessSenderKeyDistributionMessage(
            cryptoConversationId,
            senderPeerId,
            senderDeviceId,
            distributionMessage);

        _logger.LogInformation("Processed sender key distribution for group {ConversationId} from device {DeviceId}", conversationId, sourceDeviceId);

        // Step 8: Initialize GroupConversation
        var groupState = new GroupState(
            conversationId,
            epoch: 0,
            name: invite.Name,
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
        await _groupConversationRepository.AddAsync(groupConversation, selfIdentityId, ct).ConfigureAwait(false);

        _logger.LogInformation("Group invite processed successfully for conversation {ConversationId}", conversationId);
    }
}
