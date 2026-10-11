using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Ports;

/// <summary>
/// Port for fetching a consumable PreKeyBundle when the user intends to initiate a handshake session.
/// Pops an ephemeral One-Time PreKey (OPK) if available, or returns SPK-only bundle for 3DH.
/// </summary>
public interface IPreKeyBundleProvider
{
    ValueTask<DomainResult<PreKeyBundle>> FetchConsumableBundleAsync(
        PublicIdentityId targetPeerId,
        CancellationToken ct = default);
}
