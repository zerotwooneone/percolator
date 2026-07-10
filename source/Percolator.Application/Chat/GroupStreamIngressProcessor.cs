using Percolator.Chat;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging;
using Percolator.Chat.Messaging.App;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Identity;

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

    public async Task ProcessGroupMessageAsync(Guid conversationIdBytes, Guid senderPublicIdentityIdBytes, uint epoch, byte[] ciphertext, CancellationToken ct)
    {
        var conversationId = new Percolator.Chat.Messaging.ValueObjects.ConversationId(conversationIdBytes);
        var senderPublicIdentityId = new PublicIdentityId(senderPublicIdentityIdBytes);

        // Resolve sender's PeerId from PublicIdentityId
        var senderPeerId = await _peerIdentityQueries.GetPeerIdByPublicIdentityIdAsync(senderPublicIdentityId, ct).ConfigureAwait(false);
        if (senderPeerId is null)
        {
            // Unknown sender, drop the message
            return;
        }

        // Load the group aggregate for validation
        // We need a ChatSelfId for the repository - use a default for now (this is a relay context)
        var selfIdentityId = new ChatSelfId(1); // TODO: This should come from context
        var groupConversation = await _groupConversationRepository.GetByIdAsync(conversationId, selfIdentityId, ct).ConfigureAwait(false);
        if (groupConversation is null)
        {
            // Unknown conversation, drop the message
            return;
        }

        // DDD Validation: Verify sender is an active member
        var chatPublicIdentityId = new Percolator.Chat.GroupLedger.PublicIdentityId(senderPublicIdentityId.Value);
        var senderParticipantId = new RemoteParticipantId(chatPublicIdentityId, new ChatPeerId(senderPeerId.Value.Value));
        if (!groupConversation.Members.Any(m => m.ParticipantId == senderParticipantId))
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
        var senderCryptoPublicIdentity = new CryptoPublicIdentity(senderPublicIdentityIdBytes);
        var senderDeviceId = new Percolator.Cryptography.Primitives.DeviceId(1); // TODO: This should come from context or message metadata
        var conversationCryptoId = new Percolator.Cryptography.Primitives.ConversationId(conversationIdBytes);
        
        var plaintext = _senderKeyCryptographyService.DecryptGroupMessage(
            conversationCryptoId,
            senderCryptoPublicIdentity,
            senderDeviceId,
            ciphertext);

        var content = System.Text.Encoding.UTF8.GetString(plaintext);

        // Persist the decrypted message
        var publicMessageId = new PublicMessageId(Guid.NewGuid());
        var sentAt = DateTimeOffset.UtcNow; // TODO: This should come from message metadata

        await _chatMessageWriter.AddGroupMessageAsync(
            conversationId,
            senderParticipantId,
            content,
            publicMessageId,
            sentAt,
            ct).ConfigureAwait(false);
    }
}
