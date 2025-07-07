namespace Percolator.Cryptography;

public record PreKeyBundle
{
    public PublicKey IdentitySigningKey { get; }
    public PublicKey IdentityAgreementKey { get; }
    public Signature SignedPreKeySignature { get; }
    public PublicKey SignedPreKey { get; }
    public PublicKey? OneTimePreKey { get; }

    public PreKeyBundle(
        byte[] identitySigningKey,
        byte[] identityAgreementKey,
        Signature signedPreKeySignature,
        byte[] signedPreKey,
        byte[]? oneTimePreKey)
    {
        IdentitySigningKey = new PublicKey(identitySigningKey);
        IdentityAgreementKey = new PublicKey(identityAgreementKey);
        SignedPreKeySignature = signedPreKeySignature;
        SignedPreKey = new PublicKey(signedPreKey);
        OneTimePreKey = oneTimePreKey is not null ? new PublicKey(oneTimePreKey) : null;
    }
}
