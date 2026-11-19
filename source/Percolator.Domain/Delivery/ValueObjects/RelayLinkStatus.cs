namespace Percolator.Domain.Delivery.ValueObjects;

/// <summary>
/// Operational status of a persona's mailbox binding to a relay.
/// Enabled bindings are actively provisioned, maintained with OPKs, and drained.
/// Disabled bindings are retained in the vault but silenced from network traffic.
/// </summary>
public enum RelayLinkStatus
{
    Enabled = 1,
    Disabled = 2
}
