namespace Percolator.Cryptography;

public sealed class ProfileCryptographyService : IProfileCryptographyService
{
    public ProfileEncryptionResult EncryptData(ProfilePlaintextBytes serializedProfileData, ProfileKeyBytes key)
    {
        var ciphertextWithTag = CryptoUtils.EncryptAesGcm(
            serializedProfileData.Span.ToArray(),
            key.Span.ToArray(),
            null);
        
        // Extract nonce, ciphertext, and tag
        var nonce = ciphertextWithTag[..12];
        var nonceBytes = ProfileNonceBytes.FromBytesOwned(nonce);
        var ciphertext = ciphertextWithTag[12..^16];
        var tag = ciphertextWithTag[^16..];
        
        return new ProfileEncryptionResult(
            EncryptedProfileDataBytes.FromBytesOwned(ciphertext),
            nonceBytes,
            ProfileTagBytes.FromBytesOwned(tag));
    }

    public ProfilePlaintextBytes DecryptData(EncryptedProfileDataBytes ciphertext, ProfileNonceBytes nonce, ProfileTagBytes tag, ProfileKeyBytes key)
    {
        // Reconstruct the ciphertext + nonce + tag format expected by CryptoUtils
        var ciphertextWithTag = new byte[12 + ciphertext.Span.Length + 16];
        nonce.Span.CopyTo(ciphertextWithTag);
        ciphertext.Span.CopyTo(ciphertextWithTag.AsSpan(12));
        tag.Span.CopyTo(ciphertextWithTag.AsSpan(12 + ciphertext.Span.Length));
        
        var plaintext = CryptoUtils.DecryptAesGcm(ciphertextWithTag, key.Span.ToArray(), null);
        return ProfilePlaintextBytes.FromBytesOwned(plaintext);
    }
}
