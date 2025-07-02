using System.Security.Cryptography;

namespace Percolator.Cryptography;

public class X3DHManager : IX3DHManager
{
    private const int KeySize = 32;

    public HandshakeInitiationResult InitiateHandshake(PreKeyBundle remoteBundle, ECDsa identitySigningKey, ECDiffieHellman identityAgreementKey)
    {
        // Step 1: Verify the signature on the signed pre-key.
        using var remoteIdentityKey = ECDsa.Create();
        remoteIdentityKey.ImportSubjectPublicKeyInfo(remoteBundle.IdentityKey, out _);

        if (!remoteIdentityKey.VerifyData(remoteBundle.SignedPreKey, remoteBundle.Signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new CryptographicException("Invalid signature for signed pre-key.");
        }

        // Generate ephemeral keys for the initiator
        using var ephemeralKeyPair = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // Step 2: Perform the 4 DH calculations to derive the shared secret.
        using var remoteIdentityKeyECDH = ECDiffieHellman.Create();
        remoteIdentityKeyECDH.ImportSubjectPublicKeyInfo(remoteBundle.IdentityKey, out _);

        using var remoteSignedPreKey = ECDiffieHellman.Create();
        remoteSignedPreKey.ImportSubjectPublicKeyInfo(remoteBundle.SignedPreKey, out _);

        // DH1 = DH(IK_A, SPK_B)
        var dh1 = identityAgreementKey.DeriveKeyMaterial(remoteSignedPreKey.PublicKey);
        // DH2 = DH(EK_A, IK_B)
        var dh2 = ephemeralKeyPair.DeriveKeyMaterial(remoteIdentityKeyECDH.PublicKey);
        // DH3 = DH(EK_A, SPK_B)
        var dh3 = ephemeralKeyPair.DeriveKeyMaterial(remoteSignedPreKey.PublicKey);

        var dh4 = Array.Empty<byte>();
        if (remoteBundle.OneTimePreKey.Length > 0)
        {
            using var remoteOneTimePreKey = ECDiffieHellman.Create();
            remoteOneTimePreKey.ImportSubjectPublicKeyInfo(remoteBundle.OneTimePreKey, out _);
            // DH4 = DH(EK_A, OPK_B)
            dh4 = ephemeralKeyPair.DeriveKeyMaterial(remoteOneTimePreKey.PublicKey);
        }

        var combined = new byte[dh1.Length + dh2.Length + dh3.Length + dh4.Length];
        Buffer.BlockCopy(dh1, 0, combined, 0, dh1.Length);
        Buffer.BlockCopy(dh2, 0, combined, dh1.Length, dh2.Length);
        Buffer.BlockCopy(dh3, 0, combined, dh1.Length + dh2.Length, dh3.Length);
        Buffer.BlockCopy(dh4, 0, combined, dh1.Length + dh2.Length + dh3.Length, dh4.Length);

        // Step 3: Use a KDF to create the final shared key.
        var sharedSecret = new SharedSecret(HKDF.DeriveKey(HashAlgorithmName.SHA256, combined, KeySize, salt: new byte[KeySize], info: "Percolator-X3DH-v1"u8.ToArray()));
        var ephemeralPublicKey = new PublicKey(ephemeralKeyPair.PublicKey.ExportSubjectPublicKeyInfo());

        return new HandshakeInitiationResult(sharedSecret, ephemeralPublicKey);
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
