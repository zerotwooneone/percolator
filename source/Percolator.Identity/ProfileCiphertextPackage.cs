namespace Percolator.Identity;

public sealed record ProfileCiphertextPackage(
    EncryptedProfileDataBytes Ciphertext,
    ProfileNonceBytes Nonce,
    ProfileTagBytes Tag);
