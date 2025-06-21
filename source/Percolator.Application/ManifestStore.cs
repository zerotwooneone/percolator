using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;
using Percolator.Contracts.Protos; 
using System.IO;
using System;

namespace Percolator.Application
{
    public class ManifestStore
    {
        private readonly string _manifestDirectory;
        private readonly ConcurrentDictionary<ByteString, SignedManifest> _manifests = new();

        public ManifestStore()
        {
            _manifestDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Percolator", "manifests");
            Directory.CreateDirectory(_manifestDirectory);
            LoadManifestsFromDisk();
        }

        private void LoadManifestsFromDisk()
        {
            Console.WriteLine($"[ManifestStore] Loading manifests from {_manifestDirectory}...");
            var manifestFiles = Directory.GetFiles(_manifestDirectory);
            foreach (var file in manifestFiles)
            {
                try
                {
                    // Filename is the Base64 representation of the hash, with '/' replaced by '_'
                    var fileName = Path.GetFileName(file);
                    var hashBase64 = fileName.Replace('_', '/');
                    var hash = ByteString.FromBase64(hashBase64);
                    var manifestBytes = File.ReadAllBytes(file);
                    var manifest = SignedManifest.Parser.ParseFrom(manifestBytes);

                    // Verify the hash of the manifest content matches the filename to ensure integrity
                    var calculatedHash = ByteString.CopyFrom(SHA256.HashData(manifest.Manifest.ToByteArray()));
                    if (!hash.Equals(calculatedHash))
                    {
                        Console.WriteLine($"[ManifestStore] WARNING: Manifest file {fileName} is corrupt or mismatched. Hash does not match content. Skipping.");
                        continue;
                    }

                    if (_manifests.TryAdd(hash, manifest))
                    {
                        Console.WriteLine($"[ManifestStore] Loaded manifest {hash.ToBase64()} from disk.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ManifestStore] ERROR: Failed to load manifest from {file}. {ex.Message}");
                }
            }
        }

        public void StoreManifest(ByteString hash, SignedManifest manifest)
        {
            if (_manifests.TryAdd(hash, manifest))
            {
                try
                {
                    // Use a filename-safe version of Base64
                    var fileName = hash.ToBase64().Replace('/', '_');
                    var filePath = Path.Combine(_manifestDirectory, fileName);
                    File.WriteAllBytes(filePath, manifest.ToByteArray());
                    Console.WriteLine($"[ManifestStore] Persisted manifest {hash.ToBase64()} to disk.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ManifestStore] ERROR: Failed to persist manifest {hash.ToBase64()} to disk. {ex.Message}");
                }
            }
        }

        public SignedManifest? GetManifest(ByteString hash)
        {
            _manifests.TryGetValue(hash, out var manifest);
            return manifest;
        }

        public IEnumerable<ByteString> GetManifestHashes()
        {
            return _manifests.Keys;
        }
    }
}
