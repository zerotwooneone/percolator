using System.Security.Cryptography;

namespace Percolator.Cryptography;

public sealed class X3dhDeriver : IX3dhDeriver
{
    private const string KdfLabel_X3dh = "x3dh";
    private static readonly string P256Oid = ECCurve.NamedCurves.nistP256.Oid.Value!;

    private static string GetCurveOid(ECDiffieHellman ecdh)
    {
        // On some platforms/providers, KeySize is not reliable (e.g. CNG may report 521).
        return ecdh.ExportParameters(false).Curve.Oid.Value ?? string.Empty;
    }

    private static void EnsureP256(string name, ECDiffieHellman ecdh)
    {
        var oid = GetCurveOid(ecdh);
        if (!string.Equals(oid, P256Oid, StringComparison.Ordinal))
        {
            throw new CryptographicException($"X3DH expected P-256 for {name} but got curve OID '{oid}'");
        }
    }

    private static void EnsureSameCurve(string aName, ECDiffieHellman a, string bName, ECDiffieHellman b)
    {
        var aOid = GetCurveOid(a);
        var bOid = GetCurveOid(b);
        if (!string.Equals(aOid, bOid, StringComparison.Ordinal))
        {
            throw new CryptographicException($"X3DH curve mismatch ({aName}='{aOid}', {bName}='{bOid}')");
        }
    }
    public InitiatorResult DeriveInitiator(
        RatchetIdentityKey remoteIdentityKey,
        PreKey remoteSignedPreKey,
        OneTimeKey? remoteOneTimePreKey,
        PrivatePreKey localIdentityPrivateKey)
    {
        if (remoteIdentityKey is null || remoteIdentityKey.Span.Length == 0)
            throw new CryptographicException("remote IK missing");
        if (remoteSignedPreKey is null || remoteSignedPreKey.Span.Length == 0)
            throw new CryptographicException("remote SPK missing");
        if (localIdentityPrivateKey is null || localIdentityPrivateKey.Span.Length == 0)
            throw new CryptographicException("local IK private missing");

        using var ikA = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ikA.ImportECPrivateKey(localIdentityPrivateKey.Span, out _);
        using var ikB = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ikB.ImportSubjectPublicKeyInfo(remoteIdentityKey.Span, out _);
        using var spkB = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        spkB.ImportSubjectPublicKeyInfo(remoteSignedPreKey.Span, out _);
        using var opkB = remoteOneTimePreKey is null ? null : ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        if (opkB is not null)
        {
            opkB!.ImportSubjectPublicKeyInfo(remoteOneTimePreKey!.Span, out _);
        }

        using var ekA = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ekA_pub_spki = ekA.PublicKey.ExportSubjectPublicKeyInfo();
        var ekA_priv = ekA.ExportECPrivateKey();

        EnsureP256("IK_A", ikA);
        EnsureP256("IK_B", ikB);
        EnsureP256("SPK_B", spkB);
        if (opkB is not null) EnsureP256("OPK_B", opkB);
        EnsureP256("EK_A", ekA);

        EnsureSameCurve("IK_A", ikA, "SPK_B", spkB);
        EnsureSameCurve("EK_A", ekA, "IK_B", ikB);
        EnsureSameCurve("EK_A", ekA, "SPK_B", spkB);

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
            SharedSecret.FromBytes(irk),
            RatchetEphemeralKey.FromBytes(ekA_pub_spki),
            PrivatePreKey.FromBytes(ekA_priv),
            remoteOneTimePreKey is not null);
    }

    public ResponderResult DeriveResponder(
        RatchetIdentityKey initiatorIdentityKey,
        RatchetEphemeralKey initiatorEphemeralKey,
        PrivatePreKey localIdentityPrivateKey,
        PrivatePreKey localSignedPreKeyPrivate,
        PrivatePreKey? localOneTimePreKeyPrivate)
    {
        if (initiatorIdentityKey is null || initiatorIdentityKey.Span.Length == 0)
            throw new CryptographicException("initiator IK missing");
        if (initiatorEphemeralKey is null || initiatorEphemeralKey.Span.Length == 0)
            throw new CryptographicException("initiator EK missing");
        if (localIdentityPrivateKey is null || localIdentityPrivateKey.Span.Length == 0)
            throw new CryptographicException("local IK private missing");
        if (localSignedPreKeyPrivate is null || localSignedPreKeyPrivate.Span.Length == 0)
            throw new CryptographicException("local SPK private missing");

        using var ikB = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ikB.ImportECPrivateKey(localIdentityPrivateKey.Span, out _);
        using var spkB = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        spkB.ImportECPrivateKey(localSignedPreKeyPrivate.Span, out _);
        using var otkB = localOneTimePreKeyPrivate is null ? null : ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        if (otkB is not null)
        {
            otkB!.ImportECPrivateKey(localOneTimePreKeyPrivate!.Span, out _);
        }

        using var ikA = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ikA.ImportSubjectPublicKeyInfo(initiatorIdentityKey.Span, out _);
        using var ekA = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ekA.ImportSubjectPublicKeyInfo(initiatorEphemeralKey.Span, out _);

        EnsureP256("IK_B", ikB);
        EnsureP256("SPK_B", spkB);
        if (otkB is not null) EnsureP256("OTK_B", otkB);
        EnsureP256("IK_A", ikA);
        EnsureP256("EK_A", ekA);

        EnsureSameCurve("SPK_B", spkB, "IK_A", ikA);
        EnsureSameCurve("IK_B", ikB, "EK_A", ekA);
        EnsureSameCurve("SPK_B", spkB, "EK_A", ekA);

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

        return new ResponderResult(SharedSecret.FromBytes(irk), localOneTimePreKeyPrivate is not null);
    }
}
