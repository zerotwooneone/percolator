using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Contracts.Protos;

namespace Percolator.Application
{
    public class ManifestStore : IManifestStore
    {
        private static readonly string ManifestDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Percolator", "Manifests");
        private readonly ILogger<ManifestStore> _logger;
        // The string is the nullable root path. Only manifests created locally will have one.
        private readonly ConcurrentDictionary<ByteString, (SignedManifest Manifest, string? RootPath)> _manifests = new();

        public ManifestStore(ILogger<ManifestStore> logger)
        {
            _logger = logger;
            Directory.CreateDirectory(ManifestDirectory);
            LoadManifestsFromDisk();
        }

        public void Add(ByteString hash, SignedManifest manifest, string? rootPath = null)
        {
            if (_manifests.TryAdd(hash, (manifest, rootPath)))
            {
                try
                {
                    // Use a filename-safe version of Base64
                    var fileName = hash.ToBase64().Replace('/', '_');
                    var filePath = Path.Combine(ManifestDirectory, fileName);
                    File.WriteAllBytes(filePath, manifest.ToByteArray());

                    if (rootPath is not null)
                    {
                        File.WriteAllText($"{filePath}.meta", rootPath);
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogError(ex, "Failed to write manifest {Hash} to disk.", hash.ToBase64());
                }
            }
        }

        public SignedManifest? Get(ByteString hash)
        {
            return _manifests.TryGetValue(hash, out var value) ? value.Manifest : null;
        }

        private void LoadManifestsFromDisk()
        {
            _logger.LogInformation("Loading manifests from {ManifestDirectory}...", ManifestDirectory);
            var manifestFiles = Directory.GetFiles(ManifestDirectory).Where(f => !f.EndsWith(".meta"));
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
                        _logger.LogWarning("Manifest file {fileName} is corrupt or mismatched. Hash does not match content. Skipping.", fileName);
                        continue;
                    }

                    if (_manifests.TryAdd(hash, (manifest, rootPath)))
                    {
                        _logger.LogInformation("Loaded manifest {hash.ToBase64()} from disk. Root path: {(rootPath ?? \"N/A\")}", hash.ToBase64(), (rootPath ?? "N/A"));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load manifest from {file}.", file);
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
                    var filePath = Path.Combine(ManifestDirectory, fileName);
                    File.WriteAllBytes(filePath, manifest.ToByteArray());

                    if (rootPath is not null)
                    {
                        var metaFile = filePath + ".meta";
                        File.WriteAllText(metaFile, rootPath);
                    }
                    _logger.LogInformation("Persisted manifest {hash.ToBase64()} to disk.", hash.ToBase64());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to persist manifest {hash.ToBase64()} to disk.", hash.ToBase64());
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
