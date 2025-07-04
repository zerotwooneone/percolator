namespace Percolator.Cryptography;

public record PreKeyBundle(byte[] IdentityAgreementKey, byte[] IdentitySigningKey, byte[] SignedPreKey, byte[] Signature, byte[]? OneTimePreKey);
