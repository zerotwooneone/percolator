using System.Security.Cryptography;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatedPeerKeyFactory
{
    bool EnsureReverseSignalKeys(SimulatedPeerReverseSignalKeysDto keys);
}

public sealed class SimulatedPeerKeyFactory : ISimulatedPeerKeyFactory
{
    public bool EnsureReverseSignalKeys(SimulatedPeerReverseSignalKeysDto keys)
    {
        if (keys is null) throw new ArgumentNullException(nameof(keys));

        if (keys.IdentitySigningKeyPrivateKeyEcPrivateKey.Length != 0 && keys.IdentitySigningKeySpki.Length != 0)
        {
            try
            {
                using var ecdh = ECDiffieHellman.Create();
                ecdh.ImportECPrivateKey(keys.IdentitySigningKeyPrivateKeyEcPrivateKey, out _);

                if (ecdh.KeySize != 256)
                {
                    throw new CryptographicException("Reverse-signal key is not P-256");
                }

                // Ensure the stored SPKI matches the private key. If it doesn't, overwrite it.
                var spkiFromPriv = ecdh.ExportSubjectPublicKeyInfo();
                if (!spkiFromPriv.AsSpan().SequenceEqual(keys.IdentitySigningKeySpki))
                {
                    keys.IdentitySigningKeySpki = spkiFromPriv;
                    return true;
                }

                return false;
            }
            catch
            {
                // Fall through and regenerate.
            }
        }

        if (keys.IdentitySigningKeyPrivateKeyPkcs8.Length != 0)
        {
            try
            {
                using var ecdhFromPkcs8 = ECDiffieHellman.Create();
                ecdhFromPkcs8.ImportPkcs8PrivateKey(keys.IdentitySigningKeyPrivateKeyPkcs8, out _);

                if (ecdhFromPkcs8.KeySize != 256)
                {
                    throw new CryptographicException("Reverse-signal key is not P-256");
                }

                keys.IdentitySigningKeyPrivateKeyEcPrivateKey = ecdhFromPkcs8.ExportECPrivateKey();
                keys.IdentitySigningKeySpki = ecdhFromPkcs8.ExportSubjectPublicKeyInfo();
                return true;
            }
            catch
            {
                // Fall through and regenerate.
            }
        }

        using var ecdhNew = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        keys.IdentitySigningKeyPrivateKeyEcPrivateKey = ecdhNew.ExportECPrivateKey();
        keys.IdentitySigningKeySpki = ecdhNew.ExportSubjectPublicKeyInfo();
        return true;
    }
}
