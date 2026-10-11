namespace Percolator.Apps.Discovery.Features.RelayDirectoryHosting;

/// <summary>
/// Sovereign discovery policy set by the hosting relay/node.
/// </summary>
public enum RelayDiscoveryPolicy
{
    /// <summary>
    /// Discovery is completely disabled. All discovery queries are rejected or return empty.
    /// Default for public WAN relays to prevent network scraping.
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// Open discovery for friendly or local cooperative groups (e.g., LAN parties or private rooms).
    /// Peers who opted in to discoverability are discoverable by other connected peers.
    /// </summary>
    Open = 1,

    /// <summary>
    /// Privacy-preserving rendezvous using deterministic epoch-windowed blinded contact locators.
    /// Third parties cannot enumerate peers; only mutual contacts with a shared secret can resolve each other.
    /// </summary>
    BlindedContactsOnly = 2
}
