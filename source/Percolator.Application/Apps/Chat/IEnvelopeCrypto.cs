using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Chat.Primitives;

namespace Percolator.Application.Apps.Chat
{
    public interface IEnvelopeCrypto
    {
        Task<(byte[] chainKey, byte[] signingKey)> DecryptAsync(Guid conversationId, EncryptedGroupKey envelope, CancellationToken cancellationToken);
    }
}
