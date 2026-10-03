using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Security.Model;

/// <summary>
/// Aggregate root representing the client-side cryptographic credentials for a group channel.
/// Manages the group master key, authentication credential MAC, and derived blob key under memory zeroization guarantees.
/// </summary>
public sealed class GroupCredentials : AggregateRoot<ChannelId>, ISensitiveSecret
{
    public override ChannelId Id { get; }
    public GroupMasterKey MasterKey { get; private set; }
    public AuthCredentialMacBytes AuthCredentialMac { get; private set; }
    public BlobKey? LocalBlobKey { get; private set; }
    public bool IsZeroized { get; private set; }

    public GroupCredentials(
        ChannelId channelId,
        GroupMasterKey masterKey,
        AuthCredentialMacBytes authCredentialMac,
        BlobKey? localBlobKey = null)
    {
        Id = channelId;
        MasterKey = masterKey ?? throw new ArgumentNullException(nameof(masterKey));
        AuthCredentialMac = authCredentialMac ?? throw new ArgumentNullException(nameof(authCredentialMac));
        LocalBlobKey = localBlobKey;
    }

    public static DomainResult<GroupCredentials> CreateGenesis(
        ChannelId channelId,
        GroupMasterKey masterKey,
        AuthCredentialMacBytes authCredentialMac,
        BlobKey? localBlobKey = null)
    {
        if (masterKey == null)
        {
            return DomainResult<GroupCredentials>.Failure(new DomainError(
                "INVALID_MASTER_KEY", "Group master key cannot be null."));
        }

        if (authCredentialMac == null || authCredentialMac.Span.IsEmpty)
        {
            return DomainResult<GroupCredentials>.Failure(new DomainError(
                "INVALID_AUTH_MAC", "Auth credential MAC cannot be empty."));
        }

        var credentials = new GroupCredentials(channelId, masterKey, authCredentialMac, localBlobKey);
        return DomainResult<GroupCredentials>.Success(credentials);
    }

    public void UpdateAuthCredentialMac(AuthCredentialMacBytes newAuthCredentialMac)
    {
        ArgumentNullException.ThrowIfNull(newAuthCredentialMac);
        AuthCredentialMac = newAuthCredentialMac;
    }

    public void SetBlobKey(BlobKey blobKey)
    {
        ArgumentNullException.ThrowIfNull(blobKey);
        LocalBlobKey?.Dispose();
        LocalBlobKey = blobKey;
    }

    public void Zeroize()
    {
        if (IsZeroized) return;
        MasterKey?.Dispose();
        LocalBlobKey?.Dispose();
        IsZeroized = true;
    }

    public void Dispose()
    {
        Zeroize();
    }
}
