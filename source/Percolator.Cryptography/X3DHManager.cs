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

    public SharedSecret InitiateHandshake(X3dPreKeyBundle remoteBundle, ECDiffieHellman ephemeralKey,
        ECDiffieHellman identitySigningKey)
    {
        // --- DH1 ---
        if (_options.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("INITIATOR DH1 INPUTS: identitySigningKey={IdentitySigningKey}, remoteSignedPreKey={RemoteSignedPreKey}",
                Convert.ToBase64String(identitySigningKey.ExportSubjectPublicKeyInfo()),
                Convert.ToBase64String(remoteBundle.EphemeralKey.Value));
        }

        var dh1 = identitySigningKey.DeriveKeyFromHash(remoteBundle.EphemeralKey.ToEcdhPublicKey(),
            HashAlgorithmName.SHA256);
        if (_options.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("INITIATOR DH1={Secret}",
                Convert.ToBase64String(dh1));
        }

        // --- DH2 ---
        if (_options.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("INITIATOR DH2 INPUTS: ephemeralKey={EphemeralKey}, remoteIdentitySigningKey={RemoteIdentitySigningKey}",
                Convert.ToBase64String(ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo()),
                Convert.ToBase64String(remoteBundle.IdentitySigningKey.Value));
        }

        var dh2 = ephemeralKey.DeriveKeyFromHash(remoteBundle.IdentitySigningKey.ToEcdhPublicKey(),
            HashAlgorithmName.SHA256);
        if (_options.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("INITIATOR DH2={Secret}",
                Convert.ToBase64String(dh2));
        }

        // --- DH3 ---
        if (_options.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("INITIATOR DH3 INPUTS: ephemeralKey={EphemeralKey}, remoteSignedPreKey={RemoteSignedPreKey}",
                Convert.ToBase64String(ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo()),
                Convert.ToBase64String(remoteBundle.EphemeralKey.Value));
        }

        var dh3 = ephemeralKey.DeriveKeyFromHash(remoteBundle.EphemeralKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
        if (_options.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("INITIATOR DH3={Secret}",
                Convert.ToBase64String(dh3));
        }

        var dh4 = Array.Empty<byte>();
        if (remoteBundle.OneTimePreKey is not null)
        {
            // --- DH4 ---
            if (_options.EnableCryptographicMaterialLogging)
            {
                _logger.LogInformation("INITIATOR DH4 INPUTS: ephemeralKey={EphemeralKey}, remoteOneTimePreKey={RemoteOneTimePreKey}",
                    Convert.ToBase64String(ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo()),
                    Convert.ToBase64String(remoteBundle.OneTimePreKey.Value));
            }

            dh4 = ephemeralKey.DeriveKeyFromHash(remoteBundle.OneTimePreKey.ToEcdhPublicKey(),
                HashAlgorithmName.SHA256);
            if (_options.EnableCryptographicMaterialLogging)
            {
                _logger.LogInformation("INITIATOR DH4={Secret}",
                    Convert.ToBase64String(dh4));
            }
        }

        var combined = dh1.Concat(dh2).Concat(dh3).Concat(dh4).ToArray();
        var kdfResult = HKDF.DeriveKey(HashAlgorithmName.SHA256, combined, 32);

        if (_options.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("X3DH final shared secret: {Secret}",
                Convert.ToBase64String(kdfResult));
        }

        return new SharedSecret(kdfResult);
    }

    public SharedSecret RespondToHandshake(
        RatchetIdentityKey remoteIdentityKey, 
        RatchetEphemeralKey remoteEphemeralKey,
        RatchetIdentityKey selfIdentitySigningKey, 
        PrivatePreKey selfPreKey, 
        PrivateOneTimeKey? selfOneTimePreKey)
    {
        using var identitySigningKeyEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        identitySigningKeyEcdh.ImportECPrivateKey(selfIdentitySigningKey.Value, out _);

        using var signedPreKeyEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        signedPreKeyEcdh.ImportECPrivateKey(selfPreKey.Value, out _);
        // --- DH1 ---
        if (_options.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("RESPONDER DH1 INPUTS: signedPreKeyEcdh={signedPreKeyEcdh}, remoteIdentityKey={remoteIdentityKey}",
                Convert.ToBase64String(signedPreKeyEcdh.PublicKey.ExportSubjectPublicKeyInfo()),
                Convert.ToBase64String(remoteIdentityKey.Value));
        }

        var dh1 = signedPreKeyEcdh.DeriveKeyFromHash(remoteIdentityKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
        if (_options.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("RESPONDER DH1={Secret}",
                Convert.ToBase64String(dh1));
        }

        // --- DH2 ---
        if (_options.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("RESPONDER DH2 INPUTS: identitySigningKeyEcdh={identitySigningKeyEcdh}, remoteEphemeralKey={remoteEphemeralKey}",
                Convert.ToBase64String(
                    identitySigningKeyEcdh.PublicKey.ExportSubjectPublicKeyInfo()),
                Convert.ToBase64String(remoteEphemeralKey.Value));
        }

        var dh2 = identitySigningKeyEcdh.DeriveKeyFromHash(remoteEphemeralKey.ToEcdhPublicKey(),
            HashAlgorithmName.SHA256);
        if (_options.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("RESPONDER DH2={Secret}",
                Convert.ToBase64String(dh2));
        }

        // --- DH3 ---
        if (_options.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("RESPONDER DH3 INPUTS: signedPreKeyEcdh={signedPreKeyEcdh}, remoteEphemeralKey={remoteEphemeralKey}",
                Convert.ToBase64String(signedPreKeyEcdh.PublicKey.ExportSubjectPublicKeyInfo()),
                Convert.ToBase64String(remoteEphemeralKey.Value));
        }

        var dh3 = signedPreKeyEcdh.DeriveKeyFromHash(remoteEphemeralKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
        if (_options.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("RESPONDER DH3={Secret}",
                Convert.ToBase64String(dh3));
        }

        var dh4 = Array.Empty<byte>();
        if (selfOneTimePreKey is not null)
        {
            using var oneTimePreKeyEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            oneTimePreKeyEcdh.ImportECPrivateKey(selfOneTimePreKey.Value, out _);

            // --- DH4 ---
            if (_options.EnableCryptographicMaterialLogging)
            {
                _logger.LogInformation("RESPONDER DH4 INPUTS: oneTimePreKeyEcdh={oneTimePreKeyEcdh}, remoteEphemeralKey={remoteEphemeralKey}",
                    Convert.ToBase64String(oneTimePreKeyEcdh.PublicKey.ExportSubjectPublicKeyInfo()),
                    Convert.ToBase64String(remoteEphemeralKey.Value));
            }

            dh4 = oneTimePreKeyEcdh.DeriveKeyFromHash(remoteEphemeralKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
            if (_options.EnableCryptographicMaterialLogging)
            {
                _logger.LogInformation("RESPONDER DH4={Secret}",
                    Convert.ToBase64String(dh4));
            }
        }

        var combined = dh1.Concat(dh2).Concat(dh3).Concat(dh4).ToArray();
        var kdfResult = HKDF.DeriveKey(HashAlgorithmName.SHA256, combined, 32);

        if (_options.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("X3DH responder final shared secret : {Secret}",
                Convert.ToBase64String(kdfResult));
        }

        return new SharedSecret(kdfResult);
    }

    public Signature SignPreKey(ECDiffieHellman identitySigningKey, PreKey signedPreKey)
    {
        var keyParams = identitySigningKey.ExportParameters(true);
        using ECDsa eCDsa = ECDsa.Create(keyParams);
        var signatureBytes = eCDsa.SignData(signedPreKey.Value, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new Signature(signatureBytes);
    }

    public bool VerifySignature(RatchetIdentityKey identitySigningKey, PreKey signedPreKey, Signature signature)
    {
        using var ecDsa = ECDsa.Create();
        ecDsa.ImportSubjectPublicKeyInfo(identitySigningKey.Value, out _);
        return ecDsa.VerifyData(signedPreKey.Value, signature.Value, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
