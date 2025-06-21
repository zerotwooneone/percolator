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
        // The string is the nullable root path. Only manifests created locally will have one.
        private readonly ConcurrentDictionary<ByteString, (SignedManifest Manifest, string? RootPath)> _manifests = new();

        public ManifestStore()
        {
            _manifestDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Percolator", "manifests");
            Directory.CreateDirectory(_manifestDirectory);
            LoadManifestsFromDisk();
        }

        private void LoadManifestsFromDisk()
        {
            Console.WriteLine($"[ManifestStore] Loading manifests from {_manifestDirectory}...");
            var manifestFiles = Directory.GetFiles(_manifestDirectory).Where(f => !f.EndsWith(".meta"));
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

                    // Load associated root path if it exists
                    string? rootPath = null;
                    var metaFile = file + ".meta";
                    if (File.Exists(metaFile))
                    {
                        rootPath = File.ReadAllText(metaFile);
                    }

                    // Verify the hash of the manifest content matches the filename to ensure integrity
                    var calculatedHash = ByteString.CopyFrom(SHA256.HashData(manifest.Manifest.ToByteArray()));
                    if (!hash.Equals(calculatedHash))
                    {
                        Console.WriteLine($"[ManifestStore] WARNING: Manifest file {fileName} is corrupt or mismatched. Hash does not match content. Skipping.");
                        continue;
                    }

                    if (_manifests.TryAdd(hash, (manifest, rootPath)))
                    {
                        Console.WriteLine($"[ManifestStore] Loaded manifest {hash.ToBase64()} from disk. Root path: {(rootPath ?? "N/A")}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ManifestStore] ERROR: Failed to load manifest from {file}. {ex.Message}");
                }
            }
        }

        public void StoreManifest(ByteString hash, SignedManifest manifest, string? rootPath = null)
        {
            if (_manifests.TryAdd(hash, (manifest, rootPath)))
            {
                try
                {
                    // Use a filename-safe version of Base64
                    var fileName = hash.ToBase64().Replace('/', '_');
                    var filePath = Path.Combine(_manifestDirectory, fileName);
                    File.WriteAllBytes(filePath, manifest.ToByteArray());

                    if (rootPath is not null)
                    {
                        var metaFile = filePath + ".meta";
                        File.WriteAllText(metaFile, rootPath);
                    }
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
            _manifests.TryGetValue(hash, out var entry);
            return entry.Manifest;
        }

        public (SignedManifest? Manifest, string? RootPath) GetManifestAndRootPath(ByteString hash)
        {
            _manifests.TryGetValue(hash, out var entry);
            return (entry.Manifest, entry.RootPath);
        }

        public IEnumerable<ByteString> GetManifestHashes()
        {
            return _manifests.Keys;
        }
    }
}
