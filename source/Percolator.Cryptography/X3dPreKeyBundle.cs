namespace Percolator.Cryptography;

public record X3dPreKeyBundle(
    RatchetIdentityKey IdentitySigningKey, 
    RatchetAgreementKey IdentityAgreementKey, 
    PreKey SignedPreKey, 
    OneTimeKey? OneTimePreKey);