namespace Percolator.Cryptography;

/// <summary>
/// Domain service interface for Signal Group V2 (zkgroup) cryptographic primitives.
/// This is the sole domain entrypoint for zkgroup operations and uses only safe managed types.
/// The implementation in Percolator.Infrastructure handles all native FFI integration.
/// </summary>
public interface IGroupCryptographyService
{
    /// <summary>
    /// Generates a new GroupMasterKey from the provided randomness.
    /// </summary>
    /// <param name="randomness32">Exactly 32 bytes of cryptographically secure randomness.</param>
    /// <returns>A new GroupMasterKey.</returns>
    /// <exception cref="ArgumentException">Thrown if randomness32.Length is not exactly 32.</exception>
    GroupMasterKey GenerateGroupMasterKey(ReadOnlySpan<byte> randomness32);

    /// <summary>
    /// Derives the GroupId from a GroupMasterKey.
    /// </summary>
    /// <param name="masterKey">The GroupMasterKey to derive from.</param>
    /// <returns>The derived 32-byte GroupId.</returns>
    GroupId DeriveGroupId(GroupMasterKey masterKey);

    /// <summary>
    /// Derives the BlobKey from a GroupMasterKey.
    /// </summary>
    /// <param name="masterKey">The GroupMasterKey to derive from.</param>
    /// <returns>The derived 32-byte BlobKey.</returns>
    BlobKey DeriveBlobKey(GroupMasterKey masterKey);

    /// <summary>
    /// Serializes a GroupMasterKey to exactly 32 bytes.
    /// </summary>
    /// <param name="masterKey">The GroupMasterKey to serialize.</param>
    /// <returns>A 32-byte array containing the serialized master key.</returns>
    byte[] SerializeGroupMasterKey(GroupMasterKey masterKey);

    /// <summary>
    /// Serializes a GroupMasterKey to exactly 32 bytes into the provided buffer.
    /// </summary>
    /// <param name="masterKey">The GroupMasterKey to serialize.</param>
    /// <param name="buffer32">Destination buffer; must be exactly 32 bytes.</param>
    /// <exception cref="ArgumentException">Thrown if buffer32.Length is not exactly 32.</exception>
    void SerializeGroupMasterKey(GroupMasterKey masterKey, Span<byte> buffer32);

    /// <summary>
    /// Deserializes a GroupMasterKey from exactly 32 bytes.
    /// </summary>
    /// <param name="bytes32">The 32-byte array to deserialize from.</param>
    /// <returns>The deserialized GroupMasterKey.</returns>
    /// <exception cref="ArgumentException">Thrown if bytes32.Length is not exactly 32.</exception>
    /// <exception cref="CryptographicException">Thrown if deserialization fails due to data corruption or invalid format.</exception>
    GroupMasterKey DeserializeGroupMasterKey(ReadOnlySpan<byte> bytes32);

    /// <summary>
    /// Verifies a ZK group presentation for server-side access control.
    /// </summary>
    /// <param name="serverSecretParamsSeed">The server's secret params seed.</param>
    /// <param name="presentation">The ZK presentation bytes.</param>
    /// <param name="ciphertext">The ciphertext bytes.</param>
    /// <param name="redemptionTimeEpochSeconds">The redemption time in epoch seconds.</param>
    /// <returns>True if the presentation is valid, false otherwise.</returns>
    bool VerifyGroupPresentation(
        ReadOnlySpan<byte> serverSecretParamsSeed,
        ReadOnlySpan<byte> presentation,
        ReadOnlySpan<byte> ciphertext,
        ulong redemptionTimeEpochSeconds);
}
