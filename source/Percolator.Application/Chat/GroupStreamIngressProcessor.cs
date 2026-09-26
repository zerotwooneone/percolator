using Percolator.Chat;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging;
using Percolator.Chat.Messaging.App;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.Application.Chat;

public sealed class GroupStreamIngressProcessor : IGroupStreamIngressProcessor
{
    private readonly IGroupConversationRepository _groupConversationRepository;
    private readonly IPeerIdentityQueries _peerIdentityQueries;
    private readonly ISenderKeyCryptographyService _senderKeyCryptographyService;
    private readonly IChatMessageWriter _chatMessageWriter;

    public GroupStreamIngressProcessor(
        IGroupConversationRepository groupConversationRepository,
        IPeerIdentityQueries peerIdentityQueries,
        ISenderKeyCryptographyService senderKeyCryptographyService,
        IChatMessageWriter chatMessageWriter)
    {
        _groupConversationRepository = groupConversationRepository;
        _peerIdentityQueries = peerIdentityQueries;
        _senderKeyCryptographyService = senderKeyCryptographyService;
        _chatMessageWriter = chatMessageWriter;
    }

    public async Task ProcessGroupMessageAsync(Percolator.Chat.Messaging.ValueObjects.ConversationId conversationId, Percolator.Chat.GroupLedger.PublicIdentityId senderPublicIdentityId, GroupEpoch epoch, byte[] ciphertext, ChatSelfId selfIdentityId, uint senderDeviceId, DateTimeOffset sentAt, CancellationToken ct)
    {
        // Resolve sender's PeerId from PublicIdentityId
        var identitySenderPublicIdentityId = new Percolator.Identity.PublicIdentityId(senderPublicIdentityId.Value);
        var senderPeerId = await _peerIdentityQueries.GetPeerIdByPublicIdentityIdAsync(identitySenderPublicIdentityId, ct).ConfigureAwait(false);
        if (senderPeerId is null)
        {
            // Unknown sender, drop the message
            return;
        }

        // Load the group aggregate for validation
        var groupConversation = await _groupConversationRepository.GetByIdAsync(conversationId, ct).ConfigureAwait(false);
        if (groupConversation is null)
        {
            // Unknown conversation, drop the message
            return;
        }

        // DDD Validation: Verify sender is an active member
        var chatPublicIdentityId = new Percolator.Chat.GroupLedger.PublicIdentityId(senderPublicIdentityId.Value);
        var senderParticipantId = new RemoteParticipantId(chatPublicIdentityId, new ChatPeerId(senderPeerId.Value.Value));
        if (groupConversation.Members.All(m => m.ParticipantId != senderParticipantId))
        {
            // Sender is not a member, drop the message
            return;
        }

        // Epoch Verification: Compare incoming epoch against local ledger
        if (epoch > groupConversation.State.Epoch)
        {
            // Incoming epoch is higher than local state - sync required
            // For now, we reject the message. In a full implementation, this would trigger a sync workflow.
            throw new InvalidOperationException($"Epoch mismatch: incoming {epoch} > local {groupConversation.State.Epoch}. Sync required.");
        }

        // Decrypt the message
        var senderCryptoPublicIdentity = new CryptoPublicIdentity(senderPublicIdentityId.Value);
        var cryptoSenderDeviceId = new Percolator.Cryptography.Primitives.DeviceId(senderDeviceId);
        var conversationCryptoId = new Percolator.Cryptography.Primitives.ConversationId(conversationId.Value);
        
        var plaintext = _senderKeyCryptographyService.DecryptGroupMessage(
            conversationCryptoId,
            senderCryptoPublicIdentity,
            cryptoSenderDeviceId,
            ciphertext);

        var content = System.Text.Encoding.UTF8.GetString(plaintext);

        // Persist the decrypted message
        var publicMessageId = new PublicMessageId(Guid.NewGuid());

        await _chatMessageWriter.AddGroupMessageAsync(
            conversationId,
            senderParticipantId,
            content,
            publicMessageId,
            sentAt,
            ct).ConfigureAwait(false);
    }
}
