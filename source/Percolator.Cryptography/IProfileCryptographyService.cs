namespace Percolator.Cryptography;

public interface IProfileCryptographyService
{
    ProfileEncryptionResult EncryptData(ProfilePlaintextBytes serializedProfileData, ProfileKeyBytes key);
    ProfilePlaintextBytes DecryptData(EncryptedProfileDataBytes ciphertext, ProfileNonceBytes nonce, ProfileTagBytes tag, ProfileKeyBytes key);
}
