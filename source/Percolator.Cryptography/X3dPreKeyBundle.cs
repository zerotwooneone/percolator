namespace Percolator.Cryptography;

public record X3dPreKeyBundle(
    RatchetIdentityKey IdentitySigningKey, 
    RatchetEphemeralKey EphemeralKey, 
    OneTimeKey? OneTimePreKey);