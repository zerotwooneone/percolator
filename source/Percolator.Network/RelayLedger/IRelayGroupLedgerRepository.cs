using Percolator.Network.ValueObjects;

namespace Percolator.Network.RelayLedger;

public interface IRelayGroupLedgerRepository
{
    Task<RelayGroupLedger?> GetByIdAsync(RelayGroupId id, CancellationToken cancellationToken);
    Task CreateAsync(RelayGroupLedger ledger, CancellationToken cancellationToken);
    Task OverwriteStateAsync(RelayGroupLedger ledger, CancellationToken cancellationToken);
}
