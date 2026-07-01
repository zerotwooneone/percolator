using Microsoft.Extensions.Logging;
using Percolator.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Chat;

/// <summary>
/// Application service for provisioning groups with the outbox pattern.
/// </summary>
public sealed class GroupProvisioningAppService : IGroupProvisioningAppService
{
    private readonly IGroupCryptographyService _groupCryptographyService;
    private readonly ISenderKeyCryptographyService _senderKeyCryptographyService;
    private readonly IGroupCryptoStateRepository _groupCryptoStateRepository;
    private readonly IGroupConversationRepository _groupConversationRepository;
    private readonly ISelfIdentityQueries _selfIdentityQueries;
    private readonly ILogger<GroupProvisioningAppService> _logger;

    public GroupProvisioningAppService(
        IGroupCryptographyService groupCryptographyService,
        ISenderKeyCryptographyService senderKeyCryptographyService,
        IGroupCryptoStateRepository groupCryptoStateRepository,
        IGroupConversationRepository groupConversationRepository,
        ISelfIdentityQueries selfIdentityQueries,
        ILogger<GroupProvisioningAppService> logger)
    {
        _groupCryptographyService = groupCryptographyService;
        _senderKeyCryptographyService = senderKeyCryptographyService;
        _groupCryptoStateRepository = groupCryptoStateRepository;
        _groupConversationRepository = groupConversationRepository;
        _selfIdentityQueries = selfIdentityQueries;
        _logger = logger;
    }

    public async Task<Percolator.Chat.Messaging.ValueObjects.ConversationId> ProvisionGroupAsync(
        string? name,
        IReadOnlyList<ParticipantId> inviteeParticipantIds,
        Guid relayPublicIdentityId,
        SelfId selfIdentityId,
        CancellationToken cancellationToken = default)
    {
        // Step 1: Generate GroupMasterKey
        var groupMasterKey = _groupCryptographyService.GenerateGroupMasterKey();

        // Step 2: Derive ZK group public parameters from master key
        var zkGroupPublicParams = _groupCryptographyService.DeriveGroupPublicParams(groupMasterKey);

        // Step 3: Convert cryptography domain types to chat domain types (zero-copy)
        var relayGroupPublicParamsBytes = RelayGroupPublicParamsBytes.FromSpan(zkGroupPublicParams.Span);

        // Step 5: Generate conversation ID
        var conversationId = Percolator.Chat.Messaging.ValueObjects.ConversationId.NewId();
        
        // Step 4: Save master key securely
        var groupMasterKeyBytes = GroupMasterKeyBytes.FromSpan(groupMasterKey.Span);
        await _groupCryptoStateRepository.UpsertGroupMasterKeyAsync(conversationId,groupMasterKeyBytes, cancellationToken).ConfigureAwait(false);

        // Step 6: Create initial GroupState
        var groupState = new GroupState(
            conversationId,
            epoch: 0,
            name,
            relayGroupPublicParamsBytes,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        // Step 7: Create initial members (self as admin)
        // Resolve self identity info to get PublicIdentityId and SelfId
        var selfInfo = await _selfIdentityQueries.GetIdentityParticipantInfoAsync(selfIdentityId, cancellationToken).ConfigureAwait(false);
        if (selfInfo is null)
        {
            throw new InvalidOperationException($"Could not resolve self identity info for ID {selfIdentityId}");
        }

        var selfParticipantId = new LocalParticipantId(selfInfo.Value.PublicIdentityId, new ChatSelfId(selfInfo.Value.SelfId));
        var selfMember = new GroupMember(
            conversationId,
            selfParticipantId,
            GroupMemberRole.Admin,
            DateTimeOffset.UtcNow);

        // Step 8: Instantiate GroupConversation (this registers GroupProvisioningRequestedDomainEvent)
        var groupConversation = new GroupConversation(
            conversationId,
            groupState,
            relayPublicIdentityId,
            new[] { selfMember },
            name);

        // Step 9: Generate sender key distribution messages for members and invite them
        // TODO: Implement sender key distribution message generation
        // This requires converting chat domain types to cryptography domain types
        foreach (var inviteeParticipantId in inviteeParticipantIds)
        {
            // TODO: Generate ChatSenderKeyDistributionMessageBytes
            // var distributionBytes = ChatSenderKeyDistributionMessageBytes.FromSpan(...);
            var distributionBytes = ChatSenderKeyDistributionMessageBytes.FromBytesOwned(Array.Empty<byte>());

            groupConversation.InviteMember(inviteeParticipantId, distributionBytes);
        }

        // Step 10: Save atomically via outbox
        await _groupConversationRepository.AddWithOutboxAsync(groupConversation, selfIdentityId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Group {ConversationId} provisioned successfully with {MemberCount} members", conversationId, inviteeParticipantIds.Count + 1);

        return conversationId;
    }
}
