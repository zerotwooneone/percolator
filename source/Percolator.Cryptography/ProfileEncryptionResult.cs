namespace Percolator.Cryptography;

public sealed record ProfileEncryptionResult(
    EncryptedProfileDataBytes Ciphertext,
    ProfileNonceBytes Nonce,
    ProfileTagBytes Tag);
