using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Chat.Primitives;
using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Apps.Chat
{
    internal class GroupSenderKeyService : IGroupSenderKeyService
    {
        public Task ImportSenderKeyAsync(Guid conversationId, GroupKeyVersion keyVersion, EncryptedGroupKey encryptedKey, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("Sender key distribution cutover pending (Step 7b).");
        }
    }
}
