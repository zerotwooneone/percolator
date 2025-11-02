using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Cryptography;
using Percolator.Network;

namespace Percolator.Application.Network
{
    /// <summary>
    /// Resolves a DirectSessionId from a ratchet header public key (remote's current header key), scoped to the active self identity.
    /// </summary>
    public interface IRatchetKeySessionLookup
    {
        Task<DirectSessionId?> TryResolveAsync(RatchetEphemeralKey ratchetPublicKey, int selfIdentityId, CancellationToken cancellationToken);

        Task UpsertAsync(DirectSessionId sessionId, int selfIdentityId, RatchetEphemeralKey ratchetPublicKey, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken);
    }
}
