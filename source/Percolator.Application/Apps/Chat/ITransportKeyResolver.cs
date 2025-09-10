using System;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Application.Apps.Chat
{
    // Resolves a per-recipient symmetric transport key for decrypting KeyEnvelope payloads
    public interface ITransportKeyResolver
    {
        Task<byte[]?> GetAeadKeyAsync(Guid conversationId, CancellationToken ct);
    }
}
