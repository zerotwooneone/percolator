using Percolator.Network.ValueObjects;

namespace Percolator.Network.RelayLedger;

/// <summary>
/// Server-side authoritative ledger for a Relay group using ZK anchor.
/// Enforces epoch concurrency and stores encrypted entries blob.
/// </summary>
public sealed class RelayGroupLedger
{
    public RelayGroupId Id { get; }
    public RelayGroupEpoch CurrentEpoch { get; private set; }
    public EncryptedEntriesBlobBytes EncryptedEntriesBlob { get; private set; }
    public int ConcurrencyVersion { get; private set; }

    private RelayGroupLedger(
        RelayGroupId id,
        RelayGroupEpoch currentEpoch,
        EncryptedEntriesBlobBytes encryptedEntriesBlob,
        int concurrencyVersion)
    {
        Id = id;
        CurrentEpoch = currentEpoch;
        EncryptedEntriesBlob = encryptedEntriesBlob;
        ConcurrencyVersion = concurrencyVersion;
    }

    /// <summary>
    /// Creates a new RelayGroupLedger with the initial state.
    /// </summary>
    public static RelayGroupLedger CreateNew(RelayGroupId id, EncryptedEntriesBlobBytes initialBlob)
    {
        return new RelayGroupLedger(
            id,
            new RelayGroupEpoch(0),
            initialBlob,
            0);
    }

    /// <summary>
    /// Rehydrates a RelayGroupLedger from persistence.
    /// </summary>
    public static RelayGroupLedger Rehydrate(
        RelayGroupId id,
        RelayGroupEpoch currentEpoch,
        EncryptedEntriesBlobBytes encryptedEntriesBlob,
        int concurrencyVersion)
    {
        return new RelayGroupLedger(
            id,
            currentEpoch,
            encryptedEntriesBlob,
            concurrencyVersion);
    }

    /// <summary>
    /// Overwrites the state with a new encrypted entries blob, validating the epoch.
    /// </summary>
    /// <param name="baseEpoch">The expected current epoch before mutation.</param>
    /// <param name="newBlob">The new encrypted entries blob.</param>
    /// <exception cref="InvalidOperationException">Thrown when baseEpoch does not match CurrentEpoch.</exception>
    public void OverwriteState(RelayGroupEpoch baseEpoch, EncryptedEntriesBlobBytes newBlob)
    {
        if (baseEpoch.Value != CurrentEpoch.Value)
        {
            throw new InvalidOperationException($"Epoch conflict: expected {CurrentEpoch.Value}, got {baseEpoch.Value}");
        }

        CurrentEpoch = new RelayGroupEpoch(CurrentEpoch.Value + 1);
        EncryptedEntriesBlob = newBlob;
        ConcurrencyVersion++;
    }
}
