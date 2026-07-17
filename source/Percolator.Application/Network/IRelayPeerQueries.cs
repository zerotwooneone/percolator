using Percolator.Network;

namespace Percolator.Application.Network;

/// <summary>
/// Represents a connection between a self identity and a relay peer.
/// </summary>
public record RelayConnection(uint SelfId, uint RelayPeerId, uint SelfDeviceId);

/// <summary>
/// Query interface for retrieving relay peer information.
/// </summary>
public interface IRelayPeerQueries
{
    /// <summary>
    /// Gets all relay connections with their associated self identities.
    /// </summary>
    Task<IEnumerable<RelayConnection>> GetAllAsync(CancellationToken cancellationToken = default);
}
