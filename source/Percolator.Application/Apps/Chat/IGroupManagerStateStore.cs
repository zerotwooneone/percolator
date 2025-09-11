using System;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Application.Apps.Chat
{
    public interface IGroupManagerStateStore
    {
        Task<byte[]?> GetAsync(Guid conversationId, CancellationToken ct);
        Task SaveAsync(Guid conversationId, byte[] stateBlob, DateTimeOffset updatedAtUtc, CancellationToken ct);
    }
}
