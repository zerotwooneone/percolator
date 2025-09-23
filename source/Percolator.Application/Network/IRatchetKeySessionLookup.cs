using System.Threading;
using System.Threading.Tasks;
using Percolator.Network;

namespace Percolator.Application.Network
{
    /// <summary>
    /// Resolves a DirectSessionId from a ratchet header public key (remote's current header key), scoped to the active self identity.
    /// </summary>
    public interface IRatchetKeySessionLookup
    {
        Task<DirectSessionId?> TryResolveAsync(byte[] ratchetPublicKey, int selfIdentityId, CancellationToken cancellationToken);

        Task UpsertAsync(DirectSessionId sessionId, int selfIdentityId, byte[] ratchetPublicKey, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken);
    }
}
