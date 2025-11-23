using System.Security.Cryptography;

namespace Percolator.Cryptography;

public sealed class X3dhDeriver : IX3dhDeriver
{
    public (SharedSecret irk, RatchetEphemeralKey initiatorEphemeralPublic) DeriveInitiator(
        RatchetIdentityKey remoteIk,
        PreKey remoteSpk,
        OneTimeKey? remoteOtk,
        PrivatePreKey localIkPriv)
    {
        if (remoteIk?.Value is null || remoteIk.Value.Length == 0) throw new CryptographicException("remote IK missing");
        if (remoteSpk?.Value is null || remoteSpk.Value.Length == 0) throw new CryptographicException("remote SPK missing");
        if (localIkPriv?.Value is null || localIkPriv.Value.Length == 0) throw new CryptographicException("local IK private missing");

        using var ikA = ECDiffieHellman.Create();
        ikA.ImportECPrivateKey(localIkPriv.Value, out _);
        using var ikB = ECDiffieHellman.Create();
        ikB.ImportSubjectPublicKeyInfo(remoteIk.Value, out _);
        using var spkB = ECDiffieHellman.Create();
        spkB.ImportSubjectPublicKeyInfo(remoteSpk.Value, out _);
        using var opkB = remoteOtk is null ? null : ECDiffieHellman.Create();
        if (opkB is not null)
        {
            opkB!.ImportSubjectPublicKeyInfo(remoteOtk!.Value, out _);
        }

        using var ekA = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ekA_pub_spki = ekA.PublicKey.ExportSubjectPublicKeyInfo();

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
        var irk = CryptoUtils.KDF(null, input, "x3dh", CryptoUtils.KeySize);
        return (new SharedSecret(irk), new RatchetEphemeralKey(ekA_pub_spki));
    }

    public SharedSecret DeriveResponder(
        RatchetIdentityKey initiatorIk,
        RatchetEphemeralKey initiatorEk,
        PrivatePreKey localIkPriv,
        PrivatePreKey localSpkPriv,
        PrivatePreKey? localOtkPriv)
    {
        if (initiatorIk?.Value is null || initiatorIk.Value.Length == 0) throw new CryptographicException("initiator IK missing");
        if (initiatorEk?.Value is null || initiatorEk.Value.Length == 0) throw new CryptographicException("initiator EK missing");
        if (localIkPriv?.Value is null || localIkPriv.Value.Length == 0) throw new CryptographicException("local IK private missing");
        if (localSpkPriv?.Value is null || localSpkPriv.Value.Length == 0) throw new CryptographicException("local SPK private missing");

        using var ikB = ECDiffieHellman.Create();
        ikB.ImportECPrivateKey(localIkPriv.Value, out _);
        using var spkB = ECDiffieHellman.Create();
        spkB.ImportECPrivateKey(localSpkPriv.Value, out _);
        using var otkB = localOtkPriv is null ? null : ECDiffieHellman.Create();
        if (otkB is not null)
        {
            otkB!.ImportECPrivateKey(localOtkPriv!.Value, out _);
        }

        using var ikA = ECDiffieHellman.Create();
        ikA.ImportSubjectPublicKeyInfo(initiatorIk.Value, out _);
        using var ekA = ECDiffieHellman.Create();
        ekA.ImportSubjectPublicKeyInfo(initiatorEk.Value, out _);

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
        var irk = CryptoUtils.KDF(null, input, "x3dh", CryptoUtils.KeySize);
        return new SharedSecret(irk);
    }
}
