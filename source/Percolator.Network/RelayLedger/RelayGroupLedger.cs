using Percolator.Network.ValueObjects;

namespace Percolator.Network.RelayLedger;

/// <summary>
/// Server-side authoritative ledger for a Relay group.
/// Enforces epoch concurrency and stores encrypted group profile.
/// </summary>
public sealed class RelayGroupLedger
{
    public RelayGroupId Id { get; }
    public RelayGroupEpoch CurrentEpoch { get; private set; }
    public RelayGroupPublicParamsBytes PublicParams { get; private set; }
    public RelayProfileBytes EncryptedProfile { get; private set; }
    public int ConcurrencyVersion { get; private set; }

    public RelayGroupLedger(
        RelayGroupId id,
        RelayGroupEpoch currentEpoch,
        RelayGroupPublicParamsBytes publicParams,
        RelayProfileBytes encryptedProfile,
        int concurrencyVersion)
    {
        Id = id;
        CurrentEpoch = currentEpoch;
        PublicParams = publicParams;
        EncryptedProfile = encryptedProfile;
        ConcurrencyVersion = concurrencyVersion;
    }

    /// <summary>
    /// Applies a structural mutation to the group ledger.
    /// </summary>
    /// <param name="baseEpoch">The expected current epoch before mutation.</param>
    /// <param name="newProfile">The new encrypted profile after mutation.</param>
    /// <exception cref="InvalidOperationException">Thrown when baseEpoch does not match CurrentEpoch.</exception>
    public void ApplyMutation(RelayGroupEpoch baseEpoch, RelayProfileBytes newProfile)
    {
        if (baseEpoch.Value != CurrentEpoch.Value)
        {
            throw new InvalidOperationException($"Epoch conflict: expected {CurrentEpoch.Value}, got {baseEpoch.Value}");
        }

        CurrentEpoch = new RelayGroupEpoch(CurrentEpoch.Value + 1);
        EncryptedProfile = newProfile;
        ConcurrencyVersion++;
    }
}
