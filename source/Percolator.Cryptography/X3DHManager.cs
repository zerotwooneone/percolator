using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Percolator.Cryptography;

public class X3DHManager : IX3DHManager
{
    private readonly ILogger<X3DHManager> _logger;
    private readonly CryptographyOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="X3DHManager"/> class with specified options.
    /// </summary>
    /// <param name="logger">Logger for diagnostic information.</param>
    /// <param name="options">Cryptography options controlling behavior.</param>
    public X3DHManager(
        ILogger<X3DHManager> logger, 
        IOptions<CryptographyOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public SharedSecret InitiateHandshake(PreKeyBundle remoteBundle, ECDiffieHellman ephemeralKey,
        ECDiffieHellman identityAgreementKey)
    {
        // --- DH1 ---
        if (_options.EnableCryptographicMaterialLogging && _logger != null)
        {
            _logger.LogWarning("INITIATOR DH1 INPUTS: MyKey={MyKeyHash}, RemoteKey={RemoteKeyHash}",
                Convert.ToBase64String(SHA256.HashData(identityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo())),
                Convert.ToBase64String(SHA256.HashData(remoteBundle.SignedPreKey.Value)));
        }

        var dh1 = identityAgreementKey.DeriveKeyFromHash(remoteBundle.SignedPreKey.ToEcdhPublicKey(),
            HashAlgorithmName.SHA256);
        if (_options.EnableCryptographicMaterialLogging && _logger != null)
        {
            _logger.LogWarning("INITIATOR DH1 OUTPUT: SecretHash={SecretHash}",
                Convert.ToBase64String(SHA256.HashData(dh1)));
        }

        // --- DH2 ---
        if (_options.EnableCryptographicMaterialLogging && _logger != null)
        {
            _logger.LogWarning("INITIATOR DH2 INPUTS: MyKey={MyKeyHash}, RemoteKey={RemoteKeyHash}",
                Convert.ToBase64String(SHA256.HashData(ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo())),
                Convert.ToBase64String(SHA256.HashData(remoteBundle.IdentityAgreementKey.Value)));
        }

        var dh2 = ephemeralKey.DeriveKeyFromHash(remoteBundle.IdentityAgreementKey.ToEcdhPublicKey(),
            HashAlgorithmName.SHA256);
        if (_options.EnableCryptographicMaterialLogging && _logger != null)
        {
            _logger.LogWarning("INITIATOR DH2 OUTPUT: SecretHash={SecretHash}",
                Convert.ToBase64String(SHA256.HashData(dh2)));
        }

        // --- DH3 ---
        if (_options.EnableCryptographicMaterialLogging && _logger != null)
        {
            _logger.LogWarning("INITIATOR DH3 INPUTS: MyKey={MyKeyHash}, RemoteKey={RemoteKeyHash}",
                Convert.ToBase64String(SHA256.HashData(ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo())),
                Convert.ToBase64String(SHA256.HashData(remoteBundle.SignedPreKey.Value)));
        }

        var dh3 = ephemeralKey.DeriveKeyFromHash(remoteBundle.SignedPreKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
        if (_options.EnableCryptographicMaterialLogging && _logger != null)
        {
            _logger.LogWarning("INITIATOR DH3 OUTPUT: SecretHash={SecretHash}",
                Convert.ToBase64String(SHA256.HashData(dh3)));
        }

        var dh4 = Array.Empty<byte>();
        if (remoteBundle.OneTimePreKey is not null)
        {
            // --- DH4 ---
            if (_options.EnableCryptographicMaterialLogging && _logger != null)
            {
                _logger.LogWarning("INITIATOR DH4 INPUTS: MyKey={MyKeyHash}, RemoteKey={RemoteKeyHash}",
                    Convert.ToBase64String(SHA256.HashData(ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo())),
                    Convert.ToBase64String(SHA256.HashData(remoteBundle.OneTimePreKey.Value)));
            }

            dh4 = ephemeralKey.DeriveKeyFromHash(remoteBundle.OneTimePreKey.ToEcdhPublicKey(),
                HashAlgorithmName.SHA256);
            if (_options.EnableCryptographicMaterialLogging && _logger != null)
            {
                _logger.LogWarning("INITIATOR DH4 OUTPUT: SecretHash={SecretHash}",
                    Convert.ToBase64String(SHA256.HashData(dh4)));
            }
        }

        var combined = dh1.Concat(dh2).Concat(dh3).Concat(dh4).ToArray();
        var kdfResult = HKDF.DeriveKey(HashAlgorithmName.SHA256, combined, 32);

        if (_options.EnableCryptographicMaterialLogging && _logger != null)
        {
            _logger.LogWarning("X3DH final shared secret hash: {Hash}",
                Convert.ToBase64String(SHA256.HashData(kdfResult)));
        }

        return new SharedSecret(kdfResult);
    }

    public SharedSecret RespondToHandshake(RatchetIdentityKey remoteIdentityKey, RatchetEphemeralKey remoteEphemeralKey,
        PrivateAgreementKey identityAgreementKey, PrivatePreKey signedPreKey, PrivateOneTimeKey? oneTimePreKey)
    {
        using var identityAgreementKeyEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        identityAgreementKeyEcdh.ImportECPrivateKey(identityAgreementKey.Value, out _);

        using var signedPreKeyEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        signedPreKeyEcdh.ImportECPrivateKey(signedPreKey.Value, out _);

        // --- DH1 ---
        if (_options.EnableCryptographicMaterialLogging && _logger != null)
        {
            _logger.LogWarning("RESPONDER DH1 INPUTS: MyKey={MyKeyHash}, RemoteKey={RemoteKeyHash}",
                Convert.ToBase64String(SHA256.HashData(signedPreKeyEcdh.PublicKey.ExportSubjectPublicKeyInfo())),
                Convert.ToBase64String(SHA256.HashData(remoteIdentityKey.Value)));
        }

        var dh1 = signedPreKeyEcdh.DeriveKeyFromHash(remoteIdentityKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
        if (_options.EnableCryptographicMaterialLogging && _logger != null)
        {
            _logger.LogWarning("RESPONDER DH1 OUTPUT: SecretHash={SecretHash}",
                Convert.ToBase64String(SHA256.HashData(dh1)));
        }

        // --- DH2 ---
        if (_options.EnableCryptographicMaterialLogging && _logger != null)
        {
            _logger.LogWarning("RESPONDER DH2 INPUTS: MyKey={MyKeyHash}, RemoteKey={RemoteKeyHash}",
                Convert.ToBase64String(
                    SHA256.HashData(identityAgreementKeyEcdh.PublicKey.ExportSubjectPublicKeyInfo())),
                Convert.ToBase64String(SHA256.HashData(remoteEphemeralKey.Value)));
        }

        var dh2 = identityAgreementKeyEcdh.DeriveKeyFromHash(remoteEphemeralKey.ToEcdhPublicKey(),
            HashAlgorithmName.SHA256);
        if (_options.EnableCryptographicMaterialLogging && _logger != null)
        {
            _logger.LogWarning("RESPONDER DH2 OUTPUT: SecretHash={SecretHash}",
                Convert.ToBase64String(SHA256.HashData(dh2)));
        }

        // --- DH3 ---
        if (_options.EnableCryptographicMaterialLogging && _logger != null)
        {
            _logger.LogWarning("RESPONDER DH3 INPUTS: MyKey={MyKeyHash}, RemoteKey={RemoteKeyHash}",
                Convert.ToBase64String(SHA256.HashData(signedPreKeyEcdh.PublicKey.ExportSubjectPublicKeyInfo())),
                Convert.ToBase64String(SHA256.HashData(remoteEphemeralKey.Value)));
        }

        var dh3 = signedPreKeyEcdh.DeriveKeyFromHash(remoteEphemeralKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
        if (_options.EnableCryptographicMaterialLogging && _logger != null)
        {
            _logger.LogWarning("RESPONDER DH3 OUTPUT: SecretHash={SecretHash}",
                Convert.ToBase64String(SHA256.HashData(dh3)));
        }

        var dh4 = Array.Empty<byte>();
        if (oneTimePreKey is not null)
        {
            using var oneTimePreKeyEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            oneTimePreKeyEcdh.ImportECPrivateKey(oneTimePreKey.Value, out _);

            // --- DH4 ---
            if (_options.EnableCryptographicMaterialLogging && _logger != null)
            {
                _logger.LogWarning("RESPONDER DH4 INPUTS: MyKey={MyKeyHash}, RemoteKey={RemoteKeyHash}",
                    Convert.ToBase64String(SHA256.HashData(oneTimePreKeyEcdh.PublicKey.ExportSubjectPublicKeyInfo())),
                    Convert.ToBase64String(SHA256.HashData(remoteEphemeralKey.Value)));
            }

            dh4 = oneTimePreKeyEcdh.DeriveKeyFromHash(remoteEphemeralKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
            if (_options.EnableCryptographicMaterialLogging && _logger != null)
            {
                _logger.LogWarning("RESPONDER DH4 OUTPUT: SecretHash={SecretHash}",
                    Convert.ToBase64String(SHA256.HashData(dh4)));
            }
        }

        var combined = dh1.Concat(dh2).Concat(dh3).Concat(dh4).ToArray();
        var kdfResult = HKDF.DeriveKey(HashAlgorithmName.SHA256, combined, 32);

        if (_options.EnableCryptographicMaterialLogging && _logger != null)
        {
            _logger.LogWarning("X3DH responder final shared secret hash: {Hash}",
                Convert.ToBase64String(SHA256.HashData(kdfResult)));
        }

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
