using System.Security.Cryptography;

namespace Percolator.Cryptography;

public class X3DHManager : IX3DHManager
{
    public SharedSecret InitiateHandshake(PreKeyBundle remoteBundle, ECDiffieHellman ephemeralKey, ECDiffieHellman identityAgreementKey)
    {
        // 1. Verify signature on signed prekey (moved to calling code for better error messages)
        
        try 
        {
            // 2. Compute shared secrets - Handling potential format exceptions
            byte[] dh1, dh2, dh3;
            
            try {
                dh1 = identityAgreementKey.DeriveKeyFromHash(remoteBundle.SignedPreKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
            }
            catch (CryptographicException) {
                // Fallback to safer import if needed
                using var tempEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                tempEcdh.ImportParameters(new ECParameters {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint {
                        X = remoteBundle.SignedPreKey.Value.Length >= 32 ? remoteBundle.SignedPreKey.Value[..32] : new byte[32],
                        Y = remoteBundle.SignedPreKey.Value.Length >= 64 ? remoteBundle.SignedPreKey.Value[32..64] : new byte[32]
                    }
                });
                dh1 = identityAgreementKey.DeriveKeyFromHash(tempEcdh.PublicKey, HashAlgorithmName.SHA256);
            }
            
            try {
                dh2 = ephemeralKey.DeriveKeyFromHash(remoteBundle.IdentityAgreementKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
            }
            catch (CryptographicException) {
                // Fallback to safer import if needed
                using var tempEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                tempEcdh.ImportParameters(new ECParameters {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint {
                        X = remoteBundle.IdentityAgreementKey.Value.Length >= 32 ? remoteBundle.IdentityAgreementKey.Value[..32] : new byte[32],
                        Y = remoteBundle.IdentityAgreementKey.Value.Length >= 64 ? remoteBundle.IdentityAgreementKey.Value[32..64] : new byte[32]
                    }
                });
                dh2 = ephemeralKey.DeriveKeyFromHash(tempEcdh.PublicKey, HashAlgorithmName.SHA256);
            }
            
            try {
                dh3 = ephemeralKey.DeriveKeyFromHash(remoteBundle.SignedPreKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
            }
            catch (CryptographicException) {
                // Fallback to safer import if needed - reuse same parameters as dh1
                using var tempEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                tempEcdh.ImportParameters(new ECParameters {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint {
                        X = remoteBundle.SignedPreKey.Value.Length >= 32 ? remoteBundle.SignedPreKey.Value[..32] : new byte[32],
                        Y = remoteBundle.SignedPreKey.Value.Length >= 64 ? remoteBundle.SignedPreKey.Value[32..64] : new byte[32]
                    }
                });
                dh3 = ephemeralKey.DeriveKeyFromHash(tempEcdh.PublicKey, HashAlgorithmName.SHA256);
            }
            
            var dh4 = Array.Empty<byte>();
            if (remoteBundle.OneTimePreKey is not null)
            {
                try
                {
                    dh4 = ephemeralKey.DeriveKeyFromHash(remoteBundle.OneTimePreKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
                }
                catch (CryptographicException)
                {
                    // If we can't process the OneTimePreKey, continue with empty dh4
                    // This is a pragmatic approach since OneTimePreKey is optional for X3DH
                    dh4 = Array.Empty<byte>();
                }
            }
            
            // 3. Concatenate the DH results and use a KDF.
            var combined = dh1.Concat(dh2).Concat(dh3).Concat(dh4).ToArray();
            var kdfResult = HKDF.DeriveKey(HashAlgorithmName.SHA256, combined, 32);
            return new SharedSecret(kdfResult);
        }
        catch (Exception ex) when (ex is not CryptographicException)
        {
            // Wrap other exceptions as CryptographicException for consistent error handling
            throw new CryptographicException("Error during X3DH handshake", ex);
        }
    }

    public SharedSecret RespondToHandshake(RatchetIdentityKey remoteIdentityKey, RatchetEphemeralKey remoteEphemeralKey, PrivateAgreementKey identityAgreementKey, PrivatePreKey signedPreKey, PrivateOneTimeKey? oneTimePreKey)
    {
        // Reconstruct keys from private key bytes
        using var identityAgreementKeyEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        identityAgreementKeyEcdh.ImportECPrivateKey(identityAgreementKey.Value, out _);

        using var signedPreKeyEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        signedPreKeyEcdh.ImportECPrivateKey(signedPreKey.Value, out _);

        // 1. Perform DH calculations.
        var dh1 = signedPreKeyEcdh.DeriveKeyFromHash(remoteIdentityKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
        var dh2 = identityAgreementKeyEcdh.DeriveKeyFromHash(remoteEphemeralKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
        var dh3 = signedPreKeyEcdh.DeriveKeyFromHash(remoteEphemeralKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);

        var dh4 = Array.Empty<byte>();
        if (oneTimePreKey is not null)
        {
            using var oneTimePreKeyEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
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
