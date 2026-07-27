namespace Percolator.Cryptography.GroupLedger;

/// <summary>
/// Aggregate root for group cryptographic credentials.
/// Stores the GroupMasterKey and AuthCredentialMac required for ZK proofs.
/// </summary>
public sealed class GroupCredentials
{
    public GroupId Id { get; }
    public GroupMasterKey MasterKey { get; }
    public AuthCredentialMacBytes AuthCredentialMac { get; }

    public GroupCredentials(
        GroupId id,
        GroupMasterKey masterKey,
        AuthCredentialMacBytes authCredentialMac)
    {
        Id = id;
        MasterKey = masterKey;
        AuthCredentialMac = authCredentialMac;
    }
}
