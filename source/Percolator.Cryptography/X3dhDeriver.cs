using System.Security.Cryptography;

namespace Percolator.Cryptography;

public sealed class X3dhDeriver : IX3dhDeriver
{
    private const string KdfLabel_X3dh = "x3dh";
    public InitiatorResult DeriveInitiator(
        RatchetIdentityKey remoteIdentityKey,
        PreKey remoteSignedPreKey,
        OneTimeKey? remoteOneTimePreKey,
        PrivatePreKey localIdentityPrivateKey)
    {
        if (remoteIdentityKey?.Value is null || remoteIdentityKey.Value.Length == 0)
            throw new CryptographicException("remote IK missing");
        if (remoteSignedPreKey?.Value is null || remoteSignedPreKey.Value.Length == 0)
            throw new CryptographicException("remote SPK missing");
        if (localIdentityPrivateKey?.Value is null || localIdentityPrivateKey.Value.Length == 0)
            throw new CryptographicException("local IK private missing");

        using var ikA = ECDiffieHellman.Create();
        ikA.ImportECPrivateKey(localIdentityPrivateKey.Value, out _);
        using var ikB = ECDiffieHellman.Create();
        ikB.ImportSubjectPublicKeyInfo(remoteIdentityKey.Value, out _);
        using var spkB = ECDiffieHellman.Create();
        spkB.ImportSubjectPublicKeyInfo(remoteSignedPreKey.Value, out _);
        using var opkB = remoteOneTimePreKey is null ? null : ECDiffieHellman.Create();
        if (opkB is not null)
        {
            opkB!.ImportSubjectPublicKeyInfo(remoteOneTimePreKey!.Value, out _);
        }

        using var ekA = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ekA_pub_spki = ekA.PublicKey.ExportSubjectPublicKeyInfo();
        var ekA_priv = ekA.ExportECPrivateKey();

        var dh1 = ikA.DeriveKeyMaterial(spkB.PublicKey); // DH(IK_A, SPK_B)
        var dh2 = ekA.DeriveKeyMaterial(ikB.PublicKey);  // DH(EK_A, IK_B)
        var dh3 = ekA.DeriveKeyMaterial(spkB.PublicKey); // DH(EK_A, SPK_B)
        byte[]? dh4 = null;
        if (opkB is not null)
        {
            dh4 = ekA.DeriveKeyMaterial(opkB.PublicKey); // DH(EK_A, OPK_B)
        }

        var concatLen = dh1.Length + dh2.Length + dh3.Length + (dh4?.Length ?? 0);
        var input = new byte[concatLen];
        Buffer.BlockCopy(dh1, 0, input, 0, dh1.Length);
        Buffer.BlockCopy(dh2, 0, input, dh1.Length, dh2.Length);
        Buffer.BlockCopy(dh3, 0, input, dh1.Length + dh2.Length, dh3.Length);
        if (dh4 is not null)
        {
            Buffer.BlockCopy(dh4, 0, input, dh1.Length + dh2.Length + dh3.Length, dh4.Length);
        }
        var irk = CryptoUtils.KDF(null, input, KdfLabel_X3dh, CryptoUtils.KeySize);

        return new InitiatorResult(
            new SharedSecret(irk),
            new RatchetEphemeralKey(ekA_pub_spki),
            new PrivatePreKey(ekA_priv),
            remoteOneTimePreKey is not null);
    }

    public ResponderResult DeriveResponder(
        RatchetIdentityKey initiatorIdentityKey,
        RatchetEphemeralKey initiatorEphemeralKey,
        PrivatePreKey localIdentityPrivateKey,
        PrivatePreKey localSignedPreKeyPrivate,
        PrivatePreKey? localOneTimePreKeyPrivate)
    {
        if (initiatorIdentityKey?.Value is null || initiatorIdentityKey.Value.Length == 0)
            throw new CryptographicException("initiator IK missing");
        if (initiatorEphemeralKey?.Value is null || initiatorEphemeralKey.Value.Length == 0)
            throw new CryptographicException("initiator EK missing");
        if (localIdentityPrivateKey?.Value is null || localIdentityPrivateKey.Value.Length == 0)
            throw new CryptographicException("local IK private missing");
        if (localSignedPreKeyPrivate?.Value is null || localSignedPreKeyPrivate.Value.Length == 0)
            throw new CryptographicException("local SPK private missing");

        using var ikB = ECDiffieHellman.Create();
        ikB.ImportECPrivateKey(localIdentityPrivateKey.Value, out _);
        using var spkB = ECDiffieHellman.Create();
        spkB.ImportECPrivateKey(localSignedPreKeyPrivate.Value, out _);
        using var otkB = localOneTimePreKeyPrivate is null ? null : ECDiffieHellman.Create();
        if (otkB is not null)
        {
            otkB!.ImportECPrivateKey(localOneTimePreKeyPrivate!.Value, out _);
        }

        using var ikA = ECDiffieHellman.Create();
        ikA.ImportSubjectPublicKeyInfo(initiatorIdentityKey.Value, out _);
        using var ekA = ECDiffieHellman.Create();
        ekA.ImportSubjectPublicKeyInfo(initiatorEphemeralKey.Value, out _);

        var dh1 = spkB.DeriveKeyMaterial(ikA.PublicKey); // DH(SPK_B, IK_A)
        var dh2 = ikB.DeriveKeyMaterial(ekA.PublicKey);  // DH(IK_B, EK_A)
        var dh3 = spkB.DeriveKeyMaterial(ekA.PublicKey); // DH(SPK_B, EK_A)
        byte[]? dh4 = null;
        if (otkB is not null)
        {
            dh4 = otkB.DeriveKeyMaterial(ekA.PublicKey); // DH(OTK_B, EK_A)
        }

        var concatLen = dh1.Length + dh2.Length + dh3.Length + (dh4?.Length ?? 0);
        var input = new byte[concatLen];
        Buffer.BlockCopy(dh1, 0, input, 0, dh1.Length);
        Buffer.BlockCopy(dh2, 0, input, dh1.Length, dh2.Length);
        Buffer.BlockCopy(dh3, 0, input, dh1.Length + dh2.Length, dh3.Length);
        if (dh4 is not null)
        {
            Buffer.BlockCopy(dh4, 0, input, dh1.Length + dh2.Length + dh3.Length, dh4.Length);
        }
        var irk = CryptoUtils.KDF(null, input, KdfLabel_X3dh, CryptoUtils.KeySize);

        return new ResponderResult(new SharedSecret(irk), localOneTimePreKeyPrivate is not null);
    }
}
