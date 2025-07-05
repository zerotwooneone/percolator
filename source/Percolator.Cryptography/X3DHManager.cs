using System.Security.Cryptography;

namespace Percolator.Cryptography;

public class X3DHManager : IX3DHManager
{
    private const int KeySize = 32;

    public SharedSecret InitiateHandshake(PreKeyBundle remoteBundle, ECDiffieHellman ephemeralKey, ECDiffieHellman identityAgreementKey)
    {
        // Step 0: Verify the signature on the signed pre-key. This is critical to prevent a MITM attack.
        if (!VerifySignature(remoteBundle.IdentitySigningKey, remoteBundle.SignedPreKey, remoteBundle.Signature))
        {
            throw new CryptographicException("Invalid signature on remote pre-key bundle.");
        }

        // The initiator has received a pre-key bundle from the responder.
        // It uses its own identity key and a newly generated ephemeral key.

        // Step 1: Load the remote peer's public keys from the bundle.
        using var remoteIdentityKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        remoteIdentityKey.ImportSubjectPublicKeyInfo(remoteBundle.IdentityAgreementKey, out _);

        using var remoteSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        remoteSignedPreKey.ImportSubjectPublicKeyInfo(remoteBundle.SignedPreKey, out _);

        // Step 2: Perform DH calculations.
        // DH1 = DH(IKA, SPKB)
        // DH2 = DH(EKA, IKB)
        // DH3 = DH(EKA, SPKB)
        var dh1 = identityAgreementKey.DeriveKeyMaterial(remoteSignedPreKey.PublicKey);
        var dh2 = ephemeralKey.DeriveKeyMaterial(remoteIdentityKey.PublicKey);
        var dh3 = ephemeralKey.DeriveKeyMaterial(remoteSignedPreKey.PublicKey);

        // Step 4: If a one-time pre-key is present, perform a fourth DH.
        // DH4 = DH(EKA, OPKB)
        var dhCalculations = new List<byte[]> { dh1, dh2, dh3 };
        if (remoteBundle.OneTimePreKey is { Length: > 0 })
        { 
            using var remoteOneTimePreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            remoteOneTimePreKey.ImportSubjectPublicKeyInfo(remoteBundle.OneTimePreKey, out _);
            var dh4 = ephemeralKey.DeriveKeyMaterial(remoteOneTimePreKey.PublicKey);
            dhCalculations.Add(dh4);
        }

        // Step 5: Combine DH results and derive the shared secret.
        var combinedDh = dhCalculations.SelectMany(b => b).ToArray();
        var kdfResult = HKDF.DeriveKey(HashAlgorithmName.SHA256, combinedDh, KeySize, salt: new byte[KeySize], info: "Percolator-X3DH-v1"u8.ToArray());

        return new SharedSecret(kdfResult);
    }

    public SharedSecret RespondToHandshake(byte[] remoteIdentityKeyBytes, byte[] remoteEphemeralKeyBytes, ECDsa identitySigningKey, ECDiffieHellman identityAgreementKey, ECDiffieHellman signedPreKey, ECDiffieHellman? oneTimePreKey)
    {
        using var remoteIdentityKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        remoteIdentityKey.ImportSubjectPublicKeyInfo(remoteIdentityKeyBytes, out _);

        using var remoteEphemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        remoteEphemeralKey.ImportSubjectPublicKeyInfo(remoteEphemeralKeyBytes, out _);

        // DH1 = DH(SPK_B, IK_A)
        var dh1 = signedPreKey.DeriveKeyMaterial(remoteIdentityKey.PublicKey);
        // DH2 = DH(IK_B, EK_A)
        var dh2 = identityAgreementKey.DeriveKeyMaterial(remoteEphemeralKey.PublicKey);
        // DH3 = DH(SPK_B, EK_A)
        var dh3 = signedPreKey.DeriveKeyMaterial(remoteEphemeralKey.PublicKey);
        
        var dh4 = Array.Empty<byte>();
        if (oneTimePreKey is not null)
        {
            // DH4 = DH(OPK_B, EK_A)
            dh4 = oneTimePreKey.DeriveKeyMaterial(remoteEphemeralKey.PublicKey);
        }

        var combined = new byte[dh1.Length + dh2.Length + dh3.Length + dh4.Length];
        Buffer.BlockCopy(dh1, 0, combined, 0, dh1.Length);
        Buffer.BlockCopy(dh2, 0, combined, dh1.Length, dh2.Length);
        Buffer.BlockCopy(dh3, 0, combined, dh1.Length + dh2.Length, dh3.Length);
        if (dh4.Length > 0)
        {
            Buffer.BlockCopy(dh4, 0, combined, dh1.Length + dh2.Length + dh3.Length, dh4.Length);
        }

        var sharedSecretBytes = HKDF.DeriveKey(HashAlgorithmName.SHA256, combined, KeySize, salt: new byte[KeySize], info: "Percolator-X3DH-v1"u8.ToArray());
        return new SharedSecret(sharedSecretBytes);
    }

    public byte[] SignPreKey(ECDsa identitySigningKey, byte[] signedPreKey)
    {
        return identitySigningKey.SignData(signedPreKey, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public bool VerifySignature(byte[] identityKey, byte[] signedPreKey, byte[] signature)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(identityKey, out _);
        return ecdsa.VerifyData(signedPreKey, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
