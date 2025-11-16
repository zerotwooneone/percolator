using System;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Cryptography;

public interface IRatchetKeyIndex
{
    Task<SessionId?> TryResolveAsync(RatchetEphemeralKey headerPublicKey, CancellationToken cancellationToken = default);

    Task UpsertAsync(SessionId sessionId, RatchetEphemeralKey headerPublicKey, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);
}
