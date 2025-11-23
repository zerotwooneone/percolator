using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application.Apps.Chat;

namespace Percolator.Infrastructure.Chat
{
    public class SqliteGroupManagerStateStore : IGroupManagerStateStore
    {
        public SqliteGroupManagerStateStore(object _) { }

        public Task<byte[]?> GetAsync(Guid conversationId, CancellationToken ct)
            => throw new NotSupportedException("Blob-based GroupManager state has been removed. Use normalized columns.");

        public Task SaveAsync(Guid conversationId, byte[] stateBlob, DateTimeOffset updatedAtUtc, CancellationToken ct)
            => throw new NotSupportedException("Blob-based GroupManager state has been removed. Use normalized columns.");
    }
}
