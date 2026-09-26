using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Chat;

public interface IGroupStreamIngressProcessor
{
    Task ProcessGroupMessageAsync(ConversationId conversationId, PublicIdentityId senderPublicIdentityId, GroupEpoch epoch, byte[] ciphertext, ChatSelfId selfIdentityId, uint senderDeviceId, DateTimeOffset sentAt, CancellationToken ct);
}
