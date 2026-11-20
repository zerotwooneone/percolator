namespace Percolator.Domain.Security.ValueObjects;

/// <summary>
/// Encapsulates the 12-byte AES-GCM nonce and 1040-byte AEAD ciphertext package (1024 bytes padded payload + 16 bytes tag).
/// </summary>
public readonly record struct ProfileCiphertextPackage(
    byte[] Nonce,
    byte[] Ciphertext);
