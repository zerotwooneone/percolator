using Percolator.Network.ValueObjects;

namespace Percolator.Network.RelayLedger;

public interface IRelayGroupLedgerRepository
{
    Task<RelayGroupLedger?> GetByIdAsync(RelayGroupId id, CancellationToken cancellationToken);
    Task ProvisionNewGroupAsync(
        RelayGroupId groupId,
        RelayGroupPublicParamsBytes publicParams,
        RelayProfileBytes encryptedProfile,
        IReadOnlyList<byte[]> routingTokens,
        CancellationToken cancellationToken);
    Task<bool> IsMemberAsync(RelayGroupId groupId, byte[] routingToken, CancellationToken cancellationToken);
    Task UpdateGroupStateAsync(
        RelayGroupLedger ledger,
        IReadOnlyList<byte[]> addRoutingTokens,
        IReadOnlyList<byte[]> removeRoutingTokens,
        CancellationToken cancellationToken);
}
