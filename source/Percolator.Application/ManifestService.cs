using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Percolator.Contracts.Protos;
using Percolator.Identity;

namespace Percolator.Application;

public class ManifestService
{
    private readonly IIdentityService _identityService;

    public ManifestService(IIdentityService identityService)
    {
        _identityService = identityService;
    }

    public (ByteString ManifestHash, SignedManifest Manifest) CreateManifestFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("The specified file does not exist.", filePath);
        }

        // 1. Get the user's identity certificate
        var identityCert = _identityService.GetDefaultIdentityCertificate();
        using var rsaPrivateKey = identityCert.GetRSAPrivateKey() ?? throw new InvalidOperationException("Identity certificate must have an exportable RSA private key.");

        // 2. Create the file entry
        var fileInfo = new FileInfo(filePath);
        using var sha256 = SHA256.Create();
        using var fileStream = File.OpenRead(filePath);
        var fileHash = sha256.ComputeHash(fileStream);

        var manifestEntry = new ManifestEntry
        {
            Path = fileInfo.Name,
            Type = ManifestEntry.Types.ManifestEntryType.File,
            Size = fileInfo.Length,
            Hash = ByteString.CopyFrom(fileHash)
        };

        // 3. Create the inner manifest, embedding the public certificate
        var manifest = new Manifest
        {
            SignerCertificateDer = ByteString.CopyFrom(identityCert.Export(X509ContentType.Cert)),
            TimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        };
        manifest.Entries.Add(manifestEntry);

        // 4. Sign the inner manifest
        var manifestBytes = manifest.ToByteArray();
        var signature = rsaPrivateKey.SignData(manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var signedManifest = new SignedManifest
        {
            Version = 1,
            Manifest = manifest,
            Signature = ByteString.CopyFrom(signature)
        };

        // 5. The hash for announcement is the hash of the inner manifest object itself.
        var manifestHash = ByteString.CopyFrom(sha256.ComputeHash(manifestBytes));

        return (manifestHash, signedManifest);
    }

    public bool VerifyManifest(SignedManifest signedManifest)
    {
        if (signedManifest.Manifest is null || signedManifest.Signature.IsEmpty)
        {
            return false;
        }

        try
        {
            // 1. Recreate the signer's certificate from the manifest data
            var signerCert = X509CertificateLoader.LoadCertificate(signedManifest.Manifest.SignerCertificateDer.ToByteArray());
            using var rsaPublicKey = signerCert.GetRSAPublicKey();

            if (rsaPublicKey is null)
            {
                return false;
            }

            // 2. Get the data that was signed
            var manifestBytes = signedManifest.Manifest.ToByteArray();
            var signatureBytes = signedManifest.Signature.ToByteArray();

            // 3. Verify the signature
            return rsaPublicKey.VerifyData(manifestBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            // Handle exceptions from invalid certificate data or other crypto errors
            return false;
        }
    }
}
