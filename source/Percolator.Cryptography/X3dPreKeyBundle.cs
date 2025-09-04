namespace Percolator.Cryptography;

public record X3dPreKeyBundle(
    RatchetIdentityKey IdentitySigningKey, 
    PreKey SignedPreKey, 
    OneTimeKey? OneTimePreKey);