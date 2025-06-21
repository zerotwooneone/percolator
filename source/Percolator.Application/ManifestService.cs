using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Percolator.Contracts.Protos;
using Percolator.Identity;
using System.Collections.Generic;

namespace Percolator.Application;

public class ManifestService
{
    private readonly IIdentityService _identityService;

    public ManifestService(IIdentityService identityService)
    {
        _identityService = identityService;
    }

    public (ByteString ManifestHash, SignedManifest Manifest) CreateManifestFromFile(string topLevelPath)
    {
        if (!File.Exists(topLevelPath) && !Directory.Exists(topLevelPath))
        {
            throw new FileNotFoundException("The specified file or directory does not exist.", topLevelPath);
        }

        // 1. Get the user's identity certificate
        var identityCert = _identityService.GetDefaultIdentityCertificate();
        using var rsaPrivateKey = identityCert.GetRSAPrivateKey() ?? throw new InvalidOperationException("Identity certificate must have an exportable RSA private key.");

        // 2. Create the list of manifest entries by recursively walking the path
        var entries = new List<ManifestEntry>();
        var topLevelAttributes = File.GetAttributes(topLevelPath);
        // The base path for calculating relative paths is the directory containing the top-level item.
        string basePath = topLevelAttributes.HasFlag(FileAttributes.Directory)
            ? topLevelPath
            : Path.GetDirectoryName(topLevelPath) ?? string.Empty;

        CreateEntriesRecursive(topLevelPath, basePath, entries);

        // 3. Create the inner manifest, embedding the public certificate
        var manifest = new Manifest
        {
            SignerCertificateDer = ByteString.CopyFrom(identityCert.Export(X509ContentType.Cert)),
            TimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        };
        manifest.Entries.AddRange(entries);

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
        using var sha256 = SHA256.Create();
        var manifestHash = ByteString.CopyFrom(sha256.ComputeHash(manifestBytes));

        return (manifestHash, signedManifest);
    }

    private void CreateEntriesRecursive(string currentPath, string basePath, List<ManifestEntry> entries)
    {
        var attributes = File.GetAttributes(currentPath);
        string relativePath = Path.GetRelativePath(basePath, currentPath);

        if (attributes.HasFlag(FileAttributes.Directory))
        {
            entries.Add(new ManifestEntry
            {
                Path = relativePath,
                Type = ManifestEntry.Types.ManifestEntryType.Directory,
            });

            // Recurse for children
            foreach (var directory in Directory.GetDirectories(currentPath))
            {
                CreateEntriesRecursive(directory, basePath, entries);
            }
            foreach (var file in Directory.GetFiles(currentPath))
            {
                CreateEntriesRecursive(file, basePath, entries);
            }
        }
        else // It's a file
        {
            var fileInfo = new FileInfo(currentPath);
            using var sha256 = SHA256.Create();
            using var fileStream = File.OpenRead(currentPath);
            var fileHash = sha256.ComputeHash(fileStream);

            entries.Add(new ManifestEntry
            {
                Path = relativePath,
                Type = ManifestEntry.Types.ManifestEntryType.File,
                Size = fileInfo.Length,
                Hash = ByteString.CopyFrom(fileHash)
            });
        }
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
