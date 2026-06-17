using Percolator.Contracts;

namespace Percolator.Application.Chat;

/// <summary>
/// Service for provisioning new groups on the Relay's encrypted ledger.
/// </summary>
public interface IRelayGroupProvisioningService
{
    /// <summary>
    /// Provisions a new group on the Relay's encrypted ledger.
    /// </summary>
    Task<ProvisionRelayGroupResponse> ProvisionAsync(ProvisionRelayGroupRequest request, CancellationToken ct = default);
}
