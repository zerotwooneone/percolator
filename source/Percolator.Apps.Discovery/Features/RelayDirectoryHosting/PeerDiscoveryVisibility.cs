namespace Percolator.Apps.Discovery.Features.RelayDirectoryHosting;

/// <summary>
/// Sovereign discoverability state chosen by the peer.
/// </summary>
public enum PeerDiscoveryVisibility
{
    /// <summary>
    /// Stealth mode: The peer uses the relay for routing and store-and-forward mailboxes,
    /// but is strictly omitted from all public discovery listings.
    /// Default for maximum privacy.
    /// </summary>
    Hidden = 0,

    /// <summary>
    /// Discoverable: The peer explicitly opts in to being discoverable on discovery-enabled relays.
    /// </summary>
    Discoverable = 1
}
