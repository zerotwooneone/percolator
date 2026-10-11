using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Ports;

/// <summary>
/// Port for checking whether a peer has an active, unexpired Signed PreKey (SPK).
/// Allows the admission gate to verify prekey health without loading full cryptographic key bytes.
/// </summary>
public interface IPreKeyAvailabilityPort
{
    ValueTask<bool> HasActiveSignedPreKeyAsync(
        PublicIdentityId peerId,
        CancellationToken ct = default);
}
