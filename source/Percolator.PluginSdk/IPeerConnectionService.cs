using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.PluginSdk;

/// <summary>
/// Result of establishing an end-to-end encrypted session with a peer.
/// Exposes the established communication ChannelId and the remote DeviceId
/// without leaking Double Ratchet cryptographic states or keys to plugins.
/// </summary>
public sealed record PeerConnectionResult(
    ChannelId ChannelId,
    DeviceId RemoteDeviceId);

/// <summary>
/// Host service exposed via PluginSdk enabling plugins to establish secure E2EE channels
/// with peers. Encapsulates consumable prekey bundle retrieval, X3DH key agreement,
/// pairwise Double Ratchet initialization, outbox delivery routing, and direct channel provisioning.
/// </summary>
public interface IPeerConnectionService
{
    Task<DomainResult<PeerConnectionResult>> ConnectToPeerAsync(
        PublicIdentityId targetPeerId,
        DeviceId? targetDeviceId = null,
        RelayEndpoint? relayEndpoint = null,
        CancellationToken ct = default);
}
