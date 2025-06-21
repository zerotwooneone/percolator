using System;
using System.IO;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Percolator.Contracts.Protos;

namespace Percolator.Application
{
    public class ManifestService
    {
        public (ByteString ManifestHash, SignedManifest Manifest) CreateManifestFromFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("The specified file does not exist.", filePath);
            }

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

            var manifest = new Manifest
            {
                // NOTE: Using a dummy author key for now.
                AuthorIdentityPublicKey = ByteString.CopyFrom(new byte[32]),
                TimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            };
            manifest.Entries.Add(manifestEntry);

            var signedManifest = new SignedManifest
            {
                Version = 1,
                Manifest = manifest,
                // NOTE: Using a dummy signature for now.
                Signature = ByteString.CopyFrom(new byte[64])
            };

            // The hash for announcement is the hash of the manifest object itself (before signing).
            var manifestBytes = signedManifest.Manifest.ToByteArray();
            var manifestHash = ByteString.CopyFrom(sha256.ComputeHash(manifestBytes));

            return (manifestHash, signedManifest);
        }
    }
}
