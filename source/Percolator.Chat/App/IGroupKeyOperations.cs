using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Chat.Primitives;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App
{
    // Bridge interface defined in the Chat domain. Implemented in Application to call into Cryptography.GroupManager.
    public interface IGroupKeyOperations
    {
        Task ImportGroupKeyAsync(Guid conversationId, GroupKeyVersion version, EncryptedGroupKey encryptedKey, CancellationToken ct);
    }
}
