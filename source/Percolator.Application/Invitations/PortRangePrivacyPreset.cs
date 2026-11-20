namespace Percolator.Application.Invitations;

/// <summary>
/// Preset options for controlling direct P2P port range disclosure in generated invitations ([CONN-10]).
/// Balances probe latency against port enumeration privacy.
/// </summary>
public enum PortRangePrivacyPreset
{
    /// <summary>Omits port from invitation; recipient probes the standard 5100-5299 range.</summary>
    DefaultRange = 0,

    /// <summary>Embeds a discrete user-specified port range (e.g. 5100-5120).</summary>
    CustomRange = 1,

    /// <summary>Pins the invitation directly to the persona's currently bound listening port.</summary>
    SinglePort = 2
}
