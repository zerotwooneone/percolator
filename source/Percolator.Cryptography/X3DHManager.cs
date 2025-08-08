using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Percolator.Cryptography;

public class X3DHManager : IX3DHManager
{
    private readonly ILogger<X3DHManager>? _logger;
    private readonly CryptographyOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="X3DHManager"/> class with default options.
    /// </summary>
    public X3DHManager() 
        : this(null, new CryptographyOptions())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="X3DHManager"/> class with specified options.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostic information.</param>
    /// <param name="options">Cryptography options controlling behavior.</param>
    public X3DHManager(ILogger<X3DHManager>? logger, CryptographyOptions options)
    {
        _logger = logger;
        _options = options ?? new CryptographyOptions();
    }

    public SharedSecret InitiateHandshake(PreKeyBundle remoteBundle, ECDiffieHellman ephemeralKey,
        ECDiffieHellman identityAgreementKey)
    {
        var dh1 = identityAgreementKey.DeriveKeyFromHash(remoteBundle.SignedPreKey.ToEcdhPublicKey(),
            HashAlgorithmName.SHA256);
        var dh2 = ephemeralKey.DeriveKeyFromHash(remoteBundle.IdentityAgreementKey.ToEcdhPublicKey(),
            HashAlgorithmName.SHA256);
        var dh3 = ephemeralKey.DeriveKeyFromHash(remoteBundle.SignedPreKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);

        var dh4 = Array.Empty<byte>();
        if (remoteBundle.OneTimePreKey is not null)
        {
            // It's better to fail loudly if the optional key is malformed than to silently ignore it.
            dh4 = ephemeralKey.DeriveKeyFromHash(remoteBundle.OneTimePreKey.ToEcdhPublicKey(),
                HashAlgorithmName.SHA256);
        }

        // 2. Concatenate the DH results and use a KDF.
        var combined = dh1.Concat(dh2).Concat(dh3).Concat(dh4).ToArray();
        var kdfResult = HKDF.DeriveKey(HashAlgorithmName.SHA256, combined, 32);

        // Log hash of final shared secret if enabled
        if (_options.EnableCryptographicMaterialLogging && _logger != null)
        {
            _logger.LogWarning("X3DH final shared secret hash: {Hash}",
                Convert.ToBase64String(SHA256.HashData(kdfResult)));
        }

        return new SharedSecret(kdfResult);
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
        
        // Log hash of derived key material if enabled
        if (_options.EnableCryptographicMaterialLogging && _logger != null)
        {
            _logger.LogWarning("X3DH responder dh1 hash: {Hash}", Convert.ToBase64String(SHA256.HashData(dh1)));
        }
        
        var dh2 = identityAgreementKeyEcdh.DeriveKeyFromHash(remoteEphemeralKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
        
        // Log hash of derived key material if enabled
        if (_options.EnableCryptographicMaterialLogging && _logger != null)
        {
            _logger.LogWarning("X3DH responder dh2 hash: {Hash}", Convert.ToBase64String(SHA256.HashData(dh2)));
        }
        
        var dh3 = signedPreKeyEcdh.DeriveKeyFromHash(remoteEphemeralKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
        
        // Log hash of derived key material if enabled
        if (_options.EnableCryptographicMaterialLogging && _logger != null)
        {
            _logger.LogWarning("X3DH responder dh3 hash: {Hash}", Convert.ToBase64String(SHA256.HashData(dh3)));
        }

        var dh4 = Array.Empty<byte>();
        if (oneTimePreKey is not null)
        {
            using var oneTimePreKeyEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            oneTimePreKeyEcdh.ImportECPrivateKey(oneTimePreKey.Value, out _);
            dh4 = oneTimePreKeyEcdh.DeriveKeyFromHash(remoteEphemeralKey.ToEcdhPublicKey(), HashAlgorithmName.SHA256);
            
            // Log hash of derived key material if enabled
            if (_options.EnableCryptographicMaterialLogging && _logger != null)
            {
                _logger.LogWarning("X3DH responder dh4 hash: {Hash}", Convert.ToBase64String(SHA256.HashData(dh4)));
            }
        }

        // 2. Concatenate the DH results and use a KDF.
        var combined = dh1.Concat(dh2).Concat(dh3).Concat(dh4).ToArray();
        var kdfResult = HKDF.DeriveKey(HashAlgorithmName.SHA256, combined, 32);
        
        // Log hash of final shared secret if enabled
        if (_options.EnableCryptographicMaterialLogging && _logger != null)
        {
            _logger.LogWarning("X3DH responder final shared secret hash: {Hash}", Convert.ToBase64String(SHA256.HashData(kdfResult)));
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
