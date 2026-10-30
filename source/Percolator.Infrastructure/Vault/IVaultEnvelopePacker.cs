namespace Percolator.Infrastructure.Vault;

using Percolator.Domain.Common;
using Percolator.Domain.Security.ValueObjects;

public enum SlotTrialOutcome { MasterUnlocked, DuressTriggered, InvalidCredentials }
public readonly record struct SlotTrialResult(SlotTrialOutcome Outcome);

internal interface IVaultEnvelopePacker
{
    DomainResult<DatabaseHeader> CreateEnvelope(
        VaultPassphrase masterPassword,
        VaultPassphrase? duressPassword,
        ReadOnlySpan<byte> databaseKeySpan);

    SlotTrialResult EvaluateSlots(
        DatabaseHeader header,
        VaultPassphrase candidatePassword,
        Span<byte> destinationDatabaseKeyBuffer);

    DomainResult<DatabaseHeader> RewrapMasterSlot(
        DatabaseHeader currentHeader,
        VaultPassphrase newPassword,
        ReadOnlySpan<byte> databaseKeySpan);

    DomainResult<DatabaseHeader> ConfigureDuressSlot(
        DatabaseHeader currentHeader,
        VaultPassphrase? duressPassword);
}
