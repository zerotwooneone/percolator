using Percolator.Network.ValueObjects;

namespace Percolator.Network.RelayLedger;

public interface IRelayGroupLedgerRepository
{
    Task<RelayGroupLedger?> GetByIdAsync(RelayGroupId id, CancellationToken cancellationToken);
    Task SaveAsync(RelayGroupLedger ledger, CancellationToken cancellationToken);
}
