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

    /// <summary>
    /// Derives the zero-knowledge group public parameters from a GroupMasterKey.
    /// These parameters are used by the relay for blind roster management and access control.
    /// </summary>
    /// <param name="masterKey">The GroupMasterKey to derive from.</param>
    /// <returns>The derived ZK group public parameters.</returns>
    public ZkGroupPublicParamsBytes DeriveGroupPublicParams(GroupMasterKey masterKey)
    {
        if (masterKey is null)
        {
            throw new ArgumentNullException(nameof(masterKey));
        }

        // Deserialize the master key to a native handle
        using var masterKeyHandle = DeserializeMasterKeyHandle(masterKey.Span);

        // Derive GroupSecretParams from the master key
        using var secretParams = Signal.Interop.SignalCrypto.DeriveGroupSecretParams(masterKeyHandle);

        // Derive GroupPublicParams from GroupSecretParams
        using var publicParams = Signal.Interop.SignalCrypto.GetGroupPublicParams(secretParams);

        // Serialize the public params to bytes
        var buffer = Signal.Interop.SignalCrypto.SerializeGroupPublicParams(publicParams);

        // Convert to domain type
        return ZkGroupPublicParamsBytes.FromBytesOwned(buffer);
    }

    public bool VerifyGroupPresentation(
        ZkPresentationBytes presentation,
        ZkServerSecretParamsSeedBytes serverSecretSeed,
        ZkGroupPublicParamsBytes groupPublicParams,
        ulong redemptionTimeEpochSeconds)
    {
        // Deserialize the presentation
        using var presentationHandle = Signal.Interop.SignalCrypto.DeserializeAuthCredentialWithPniPresentation(presentation.Span);

        // Generate server secret params from seed
        using var serverSecretParamsHandle = Signal.Interop.SignalCrypto.GenerateServerSecretParams(serverSecretSeed.Span);

        // Deserialize group public params
        using var groupPublicParamsHandle = Signal.Interop.SignalCrypto.DeserializeGroupPublicParams(groupPublicParams.Span);

        // Verify the presentation
        try
        {
            Signal.Interop.SignalCrypto.VerifyAuthCredentialWithPniPresentation(
                presentationHandle,
                serverSecretParamsHandle,
                groupPublicParamsHandle,
                redemptionTimeEpochSeconds);
            return true;
        }
        catch (Exception) // Catch the interop/verification exception 
        {
            return false;
        }
    }

    public byte[] EncryptGroupProfile(
        GroupMasterKey masterKey,
        ProfilePlaintextBytes profilePlaintext)
    {
        using var masterKeyHandle = DeserializeMasterKeyHandle(masterKey.Span);
        using var secretParams = Signal.Interop.SignalCrypto.DeriveGroupSecretParams(masterKeyHandle);
        
        var blobKeyBytes = new byte[32];
        Signal.Interop.SignalCrypto.GetBlobKey(secretParams, blobKeyBytes);
        
        using var aes = new System.Security.Cryptography.AesGcm(blobKeyBytes, 16);
        byte[] nonce = new byte[12];
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
        
        byte[] ciphertext = new byte[profilePlaintext.Length];
        byte[] tag = new byte[16];
        
        aes.Encrypt(nonce, profilePlaintext.Span, ciphertext, tag);
        
        byte[] result = new byte[12 + ciphertext.Length + 16];
        Buffer.BlockCopy(nonce, 0, result, 0, 12);
        Buffer.BlockCopy(ciphertext, 0, result, 12, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, 12 + ciphertext.Length, 16);
        
        return result;
    }

    public ProfilePlaintextBytes DecryptGroupProfile(
        GroupMasterKey masterKey,
        ReadOnlySpan<byte> ciphertextSpan)
    {
        if (ciphertextSpan.Length < 12 + 16)
        {
            throw new ArgumentException("Ciphertext too short to contain nonce and tag");
        }

        using var masterKeyHandle = DeserializeMasterKeyHandle(masterKey.Span);
        using var secretParams = Signal.Interop.SignalCrypto.DeriveGroupSecretParams(masterKeyHandle);
        
        var blobKeyBytes = new byte[32];
        Signal.Interop.SignalCrypto.GetBlobKey(secretParams, blobKeyBytes);
        
        using var aes = new System.Security.Cryptography.AesGcm(blobKeyBytes, 16);
        
        byte[] nonce = ciphertextSpan.Slice(0, 12).ToArray();
        byte[] ciphertext = ciphertextSpan.Slice(12, ciphertextSpan.Length - 28).ToArray();
        byte[] tag = ciphertextSpan.Slice(ciphertextSpan.Length - 16, 16).ToArray();
        
        byte[] plaintext = new byte[ciphertext.Length];
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        
        return ProfilePlaintextBytes.FromBytesOwned(plaintext);
    }
}
