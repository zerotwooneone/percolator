using Percolator.Chat.GroupMembership;

namespace Percolator.Application.Chat;

public interface IGroupStreamIngressProcessor
{
    Task ProcessGroupMessageAsync(Guid conversationIdBytes, Guid senderPublicIdentityIdBytes, uint epoch, byte[] ciphertext, ChatSelfId selfIdentityId, uint senderDeviceId, DateTimeOffset sentAt, CancellationToken ct);
}
