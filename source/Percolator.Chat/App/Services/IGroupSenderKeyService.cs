using Percolator.Chat.Primitives;
using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Apps.Chat
{
    public interface IGroupSenderKeyService
    {
        Task ImportSenderKeyAsync(Guid conversationId, GroupKeyVersion keyVersion, EncryptedGroupKey encryptedKey, CancellationToken cancellationToken);
    }
}
