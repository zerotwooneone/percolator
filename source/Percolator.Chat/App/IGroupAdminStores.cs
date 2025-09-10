using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App
{
    public sealed record GroupAdminKeyRecord(AdminPublicKey PublicKey, DateTimeOffset AddedAtUtc, DateTimeOffset? RevokedAtUtc);

    public interface IGroupAdminKeyStore
    {
        Task<IReadOnlyList<GroupAdminKeyRecord>> GetKeysAsync(Guid conversationId, CancellationToken ct);
        Task AddKeyAsync(Guid conversationId, AdminPublicKey publicKey, DateTimeOffset addedAtUtc, CancellationToken ct);
        Task RevokeKeyAsync(Guid conversationId, AdminPublicKey publicKey, DateTimeOffset revokedAtUtc, CancellationToken ct);
    }

    public interface IGroupAdminOpStore
    {
        // Returns true if inserted; false if duplicate (idempotent)
        Task<bool> TryAddAsync(Guid conversationId, Guid opId, DateTimeOffset appliedAtUtc, CancellationToken ct);
    }
}
