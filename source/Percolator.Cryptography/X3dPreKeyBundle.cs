namespace Percolator.Cryptography;

public record X3dPreKeyBundle(
    RatchetIdentityKey IdentitySigningKey, 
    RatchetEphemeralKey SignedPreKey, 
    OneTimeKey? OneTimePreKey);