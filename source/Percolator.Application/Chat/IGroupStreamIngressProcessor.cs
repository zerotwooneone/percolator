namespace Percolator.Application.Chat;

public interface IGroupStreamIngressProcessor
{
    Task ProcessGroupMessageAsync(Guid conversationIdBytes, Guid senderPublicIdentityIdBytes, uint epoch, byte[] ciphertext, CancellationToken ct);
}
