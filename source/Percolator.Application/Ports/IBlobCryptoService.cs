using Percolator.Domain.Common;
using Percolator.PluginSdk;

namespace Percolator.Application2.Ports;

public readonly record struct EncryptedBlobPackage(
    Stream CiphertextStream,
    byte[] CiphertextSha256,
    byte[] EncryptionKey,
    byte[] BaseNonce,
    long PlaintextSize,
    long CiphertextSize);

public interface IBlobCryptoService
{
    /// <summary>
    /// Strips EXIF metadata, applies bucket padding, generates ephemeral keys, and encrypts into a 64 KB chunked STREAM.
    /// </summary>
    ValueTask<DomainResult<EncryptedBlobPackage>> EncryptAndPackageAsync(
        Stream plaintextStream,
        string mimeType,
        CancellationToken ct = default);

    /// <summary>
    /// Decrypts a chunked STREAM AEAD payload on the fly, verifying chunk tags.
    /// </summary>
    ValueTask<DomainResult<Stream>> DecryptStreamAsync(
        Stream ciphertextStream,
        byte[] encryptionKey,
        byte[] baseNonce,
        long unpaddedPlaintextSize,
        CancellationToken ct = default);
}
