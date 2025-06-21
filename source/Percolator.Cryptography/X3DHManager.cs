using System.Security.Cryptography;

namespace Percolator.Cryptography;

public static class X3DHManager
{
    private const int KeySize = 32;

    public static byte[] InitiateHandshake(PreKeyBundle remoteBundle, ECDiffieHellman identityKey, ECDiffieHellman ephemeralKeyPair)
    {
        if (!VerifySignature(remoteBundle.IdentityKey, remoteBundle.SignedPreKey, remoteBundle.Signature))
        {
            throw new CryptographicException("Invalid signature for signed pre-key.");
        }

        using var remoteIdentityKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        remoteIdentityKey.ImportSubjectPublicKeyInfo(remoteBundle.IdentityKey, out _);

        using var remoteSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        remoteSignedPreKey.ImportSubjectPublicKeyInfo(remoteBundle.SignedPreKey, out _);

        using var remoteOneTimePreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        remoteOneTimePreKey.ImportSubjectPublicKeyInfo(remoteBundle.OneTimePreKey, out _);

        // DH1 = DH(IK_A, SPK_B)
        var dh1 = identityKey.DeriveKeyMaterial(remoteSignedPreKey.PublicKey);
        // DH2 = DH(EK_A, IK_B)
        var dh2 = ephemeralKeyPair.DeriveKeyMaterial(remoteIdentityKey.PublicKey);
        // DH3 = DH(EK_A, SPK_B)
        var dh3 = ephemeralKeyPair.DeriveKeyMaterial(remoteSignedPreKey.PublicKey);
        // DH4 = DH(EK_A, OPK_B)
        var dh4 = ephemeralKeyPair.DeriveKeyMaterial(remoteOneTimePreKey.PublicKey);

        var combined = new byte[dh1.Length + dh2.Length + dh3.Length + dh4.Length];
        Buffer.BlockCopy(dh1, 0, combined, 0, dh1.Length);
        Buffer.BlockCopy(dh2, 0, combined, dh1.Length, dh2.Length);
        Buffer.BlockCopy(dh3, 0, combined, dh1.Length + dh2.Length, dh3.Length);
        Buffer.BlockCopy(dh4, 0, combined, dh1.Length + dh2.Length + dh3.Length, dh4.Length);

        return HKDF.Extract(HashAlgorithmName.SHA256, combined, new byte[KeySize]);
    }

    public static byte[] RespondToHandshake(byte[] remoteIdentityKeyBytes, byte[] remoteEphemeralKeyBytes, ECDiffieHellman identityKey, ECDiffieHellman signedPreKey, ECDiffieHellman oneTimePreKey)
    {
        using var remoteIdentityKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        remoteIdentityKey.ImportSubjectPublicKeyInfo(remoteIdentityKeyBytes, out _);

        using var remoteEphemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        remoteEphemeralKey.ImportSubjectPublicKeyInfo(remoteEphemeralKeyBytes, out _);

        // DH1 = DH(SPK_B, IK_A)
        var dh1 = signedPreKey.DeriveKeyMaterial(remoteIdentityKey.PublicKey);
        // DH2 = DH(IK_B, EK_A)
        var dh2 = identityKey.DeriveKeyMaterial(remoteEphemeralKey.PublicKey);
        // DH3 = DH(SPK_B, EK_A)
        var dh3 = signedPreKey.DeriveKeyMaterial(remoteEphemeralKey.PublicKey);
        // DH4 = DH(OPK_B, EK_A)
        var dh4 = oneTimePreKey.DeriveKeyMaterial(remoteEphemeralKey.PublicKey);

        var combined = new byte[dh1.Length + dh2.Length + dh3.Length + dh4.Length];
        Buffer.BlockCopy(dh1, 0, combined, 0, dh1.Length);
        Buffer.BlockCopy(dh2, 0, combined, dh1.Length, dh2.Length);
        Buffer.BlockCopy(dh3, 0, combined, dh1.Length + dh2.Length, dh3.Length);
        Buffer.BlockCopy(dh4, 0, combined, dh1.Length + dh2.Length + dh3.Length, dh4.Length);

        return HKDF.Extract(HashAlgorithmName.SHA256, combined, new byte[KeySize]);
    }

    public static byte[] SignPreKey(ECDiffieHellman identityKey, byte[] signedPreKey)
    {
        var parameters = identityKey.ExportParameters(true);
        using var ecdsa = ECDsa.Create(parameters);
        return ecdsa.SignData(signedPreKey, HashAlgorithmName.SHA256);
    }

    public static bool VerifySignature(byte[] identityKey, byte[] signedPreKey, byte[] signature)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(identityKey, out _);
        return ecdsa.VerifyData(signedPreKey, signature, HashAlgorithmName.SHA256);
    }
}
