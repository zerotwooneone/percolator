using System;
using System.Linq;
using System.Security.Cryptography;

namespace Percolator.Cryptography;

public class X3DHManager : IX3DHManager
{
    public SharedSecret InitiateHandshake(PreKeyBundle remoteBundle, ECDiffieHellman ephemeralKey, ECDiffieHellman identityAgreementKey)
    {
        // 1. Verify the signature on the signed pre-key
        if (!VerifySignature(remoteBundle.IdentitySigningKey, remoteBundle.SignedPreKey, remoteBundle.SignedPreKeySignature))
        {
            throw new CryptographicException("Invalid signature for signed pre-key.");
        }

        // 2. Compute shared secrets
        var dh1 = identityAgreementKey.DeriveKeyFromHash(remoteBundle.SignedPreKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
        var dh2 = ephemeralKey.DeriveKeyFromHash(remoteBundle.IdentityAgreementKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
        var dh3 = ephemeralKey.DeriveKeyFromHash(remoteBundle.SignedPreKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);

        var dh4 = Array.Empty<byte>();
        if (remoteBundle.OneTimePreKey is not null)
        {
            dh4 = ephemeralKey.DeriveKeyFromHash(remoteBundle.OneTimePreKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
        }

        // 3. Concatenate the DH results and use a KDF.
        var combined = dh1.Concat(dh2).Concat(dh3).Concat(dh4).ToArray();
        var kdfResult = HKDF.DeriveKey(HashAlgorithmName.SHA256, combined, 32);
        return new SharedSecret(kdfResult);
    }

    public SharedSecret RespondToHandshake(RatchetIdentityKey remoteIdentityKey, RatchetEphemeralKey remoteEphemeralKey, PrivateKey identityAgreementKey, PrivateKey signedPreKey, PrivateKey? oneTimePreKey)
    {
        // Reconstruct keys from private key bytes
        using var identityAgreementKeyEcdh = ECDiffieHellman.Create();
        identityAgreementKeyEcdh.ImportECPrivateKey(identityAgreementKey.Value, out _);

        using var signedPreKeyEcdh = ECDiffieHellman.Create();
        signedPreKeyEcdh.ImportECPrivateKey(signedPreKey.Value, out _);

        // 1. Perform DH calculations.
        var dh1 = signedPreKeyEcdh.DeriveKeyFromHash(remoteIdentityKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
        var dh2 = identityAgreementKeyEcdh.DeriveKeyFromHash(remoteEphemeralKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
        var dh3 = signedPreKeyEcdh.DeriveKeyFromHash(remoteEphemeralKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);

        var dh4 = Array.Empty<byte>();
        if (oneTimePreKey is not null)
        {
            using var oneTimePreKeyEcdh = ECDiffieHellman.Create();
            oneTimePreKeyEcdh.ImportECPrivateKey(oneTimePreKey.Value, out _);
            dh4 = oneTimePreKeyEcdh.DeriveKeyFromHash(remoteEphemeralKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
        }

        // 2. Concatenate the DH results and use a KDF.
        var combined = dh1.Concat(dh2).Concat(dh3).Concat(dh4).ToArray();
        var kdfResult = HKDF.DeriveKey(HashAlgorithmName.SHA256, combined, 32);
        return new SharedSecret(kdfResult);
    }

    public Signature SignPreKey(ECDsa identitySigningKey, PreKey signedPreKey)
    {
        var signatureBytes = identitySigningKey.SignData(signedPreKey.Value, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new Signature(signatureBytes);
    }

    public bool VerifySignature(RatchetIdentityKey identitySigningKey, PreKey signedPreKey, Signature signature)
    {
        using var ecDsa = ECDsa.Create();
        ecDsa.ImportSubjectPublicKeyInfo(identitySigningKey.Value, out _);
        return ecDsa.VerifyData(signedPreKey.Value, signature.Value, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
