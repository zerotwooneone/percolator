using System.Security.Cryptography;
using Percolator.Cryptography;

namespace Percolator.Infrastructure.Cryptography;

/// <summary>
/// Infrastructure implementation of IGroupCryptographyService using Signal.Interop.
/// This service handles all native FFI integration and SafeHandle management.
/// </summary>
public sealed class ZkgroupCryptographyService : IGroupCryptographyService
{
    /// <summary>
    /// Generates a new GroupMasterKey from the provided randomness.
    /// </summary>
    public GroupMasterKey GenerateGroupMasterKey(ReadOnlySpan<byte> randomness32)
    {
        if (randomness32.Length != 32)
        {
            throw new ArgumentException("Randomness must be exactly 32 bytes.", nameof(randomness32));
        }

        // Generate GroupSecretParams from randomness
        using var secretParams = Signal.Interop.SignalCrypto.GenerateGroupSecretParams(randomness32);

        // Extract GroupMasterKey from the generated GroupSecretParams
        using var masterKeyHandle = Signal.Interop.SignalCrypto.GetGroupMasterKey(secretParams);

        // Serialize the master key to bytes
        var buffer = new byte[32];
        Signal.Interop.SignalCrypto.SerializeGroupMasterKey(masterKeyHandle, buffer);

        // Convert to domain type
        return GroupMasterKey.FromBytesOwned(buffer);
    }

    /// <summary>
    /// Derives the GroupId from a GroupMasterKey.
    /// </summary>
    public GroupId DeriveGroupId(GroupMasterKey masterKey)
    {
        if (masterKey is null)
        {
            throw new ArgumentNullException(nameof(masterKey));
        }

        // Deserialize the master key to a native handle
        using var masterKeyHandle = DeserializeMasterKeyHandle(masterKey.Span);

        // Derive GroupSecretParams from the master key
        using var secretParams = Signal.Interop.SignalCrypto.DeriveGroupSecretParams(masterKeyHandle);

        // Extract group_id from GroupSecretParams
        var buffer = new byte[32];
        Signal.Interop.SignalCrypto.GetGroupId(secretParams, buffer);

        // Convert to domain type
        return GroupId.FromBytesOwned(buffer);
    }

    /// <summary>
    /// Derives the BlobKey from a GroupMasterKey.
    /// </summary>
    public BlobKey DeriveBlobKey(GroupMasterKey masterKey)
    {
        if (masterKey is null)
        {
            throw new ArgumentNullException(nameof(masterKey));
        }

        // Deserialize the master key to a native handle
        using var masterKeyHandle = DeserializeMasterKeyHandle(masterKey.Span);

        // Derive GroupSecretParams from the master key
        using var secretParams = Signal.Interop.SignalCrypto.DeriveGroupSecretParams(masterKeyHandle);

        // Extract blob_key from GroupSecretParams
        var buffer = new byte[32];
        Signal.Interop.SignalCrypto.GetBlobKey(secretParams, buffer);

        // Convert to domain type
        return BlobKey.FromBytesOwned(buffer);
    }

    /// <summary>
    /// Serializes a GroupMasterKey to exactly 32 bytes.
    /// </summary>
    public byte[] SerializeGroupMasterKey(GroupMasterKey masterKey)
    {
        if (masterKey is null)
        {
            throw new ArgumentNullException(nameof(masterKey));
        }

        var buffer = new byte[32];
        SerializeGroupMasterKey(masterKey, buffer);
        return buffer;
    }

    /// <summary>
    /// Serializes a GroupMasterKey to exactly 32 bytes into the provided buffer.
    /// </summary>
    public void SerializeGroupMasterKey(GroupMasterKey masterKey, Span<byte> buffer32)
    {
        if (masterKey is null)
        {
            throw new ArgumentNullException(nameof(masterKey));
        }

        if (buffer32.Length != 32)
        {
            throw new ArgumentException("Buffer must be exactly 32 bytes.", nameof(buffer32));
        }

        // Deserialize the master key to a native handle
        using var masterKeyHandle = DeserializeMasterKeyHandle(masterKey.Span);

        // Serialize to the provided buffer
        Signal.Interop.SignalCrypto.SerializeGroupMasterKey(masterKeyHandle, buffer32);
    }

    /// <summary>
    /// Deserializes a GroupMasterKey from exactly 32 bytes.
    /// </summary>
    public GroupMasterKey DeserializeGroupMasterKey(ReadOnlySpan<byte> bytes32)
    {
        if (bytes32.Length != 32)
        {
            throw new ArgumentException("Input must be exactly 32 bytes.", nameof(bytes32));
        }

        // Use FromSpan to create a defensive copy
        return GroupMasterKey.FromSpan(bytes32);
    }

    private static Signal.Interop.GroupMasterKeySafeHandle DeserializeMasterKeyHandle(ReadOnlySpan<byte> bytes32)
    {
        try
        {
            return Signal.Interop.SignalCrypto.DeserializeGroupMasterKey(bytes32);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("length"))
        {
            throw new ArgumentException("Input must be exactly 32 bytes.", nameof(bytes32), ex);
        }
        catch (Exception ex) when (IsDeserializationFailure(ex))
        {
            throw new CryptographicException("Failed to deserialize the GroupMasterKey due to data corruption or invalid format.", ex);
        }
    }

    private static bool IsDeserializationFailure(Exception ex)
    {
        // Check if the exception indicates a deserialization failure
        // Signal.Interop maps status code 4 to CryptographicException with specific message
        return ex is CryptographicException && ex.Message.Contains("deserial");
    }

    public bool VerifyGroupPresentation(
        ReadOnlySpan<byte> presentation,
        ReadOnlySpan<byte> serverSecretSeed,
        ReadOnlySpan<byte> groupPublicParams,
        ulong redemptionTimeEpochSeconds)
    {
        // Deserialize the presentation
        using var presentationHandle = Signal.Interop.SignalCrypto.DeserializeAuthCredentialWithPniPresentation(presentation);

        // Generate server secret params from seed (DeserializeServerSecretParams doesn't exist in Signal.Interop)
        // The seed is used as randomness to generate the server secret params
        using var serverSecretParamsHandle = Signal.Interop.SignalCrypto.GenerateServerSecretParams(serverSecretSeed);

        // Deserialize group public params
        using var groupPublicParamsHandle = Signal.Interop.SignalCrypto.DeserializeGroupPublicParams(groupPublicParams);

        // Verify the presentation
        Signal.Interop.SignalCrypto.VerifyAuthCredentialWithPniPresentation(
            presentationHandle,
            serverSecretParamsHandle,
            groupPublicParamsHandle,
            redemptionTimeEpochSeconds);

        // If no exception is thrown, verification succeeded
        return true;
    }
}
