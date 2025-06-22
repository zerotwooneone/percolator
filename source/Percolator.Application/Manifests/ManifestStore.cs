using System.Collections.Concurrent;
using System.Security;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Contracts.Protos;

namespace Percolator.Application.Manifests
{
    public class ManifestStore : IManifestStore
    {
        private static readonly string ManifestDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Percolator", "Manifests");
        private const long MaxManifestSize = 1 * 1024 * 1024; // 1 MB
        private const long MaxTotalStoreSize = 1 * 1024 * 1024 * 1024; // 1 GB

        private readonly ILogger<ManifestStore> _logger;
        private readonly ConcurrentDictionary<ByteString, (SignedManifest Manifest, string? RootPath)> _manifests = new();
        private long _currentStoreSize;

        public ManifestStore(ILogger<ManifestStore> logger)
        {
            _logger = logger;
            Directory.CreateDirectory(ManifestDirectory);
            LoadManifestsFromDisk();
        }

        public void Add(ByteString hash, SignedManifest manifest, string? rootPath = null)
        {
            var manifestSize = manifest.CalculateSize();
            if (manifestSize == 0)
            {
                throw new ArgumentException("Manifest cannot be empty.", nameof(manifest));
            }

            if (manifestSize > MaxManifestSize)
            {
                throw new SecurityException($"Manifest rejected: size {manifestSize} exceeds limit of {MaxManifestSize}");
            }

            // Optimistically reserve space
            long newTotalSize = Interlocked.Add(ref _currentStoreSize, manifestSize);

            if (newTotalSize > MaxTotalStoreSize)
            {
                // Roll back reservation and fail
                Interlocked.Add(ref _currentStoreSize, -manifestSize);
                throw new SecurityException($"Manifest rejected: store size would exceed quota of {MaxTotalStoreSize}. Current size: {_currentStoreSize}, attempted to add {manifestSize}.");
            }

            if (!_manifests.TryAdd(hash, (manifest, rootPath)))
            {
                // Roll back reservation and fail
                Interlocked.Add(ref _currentStoreSize, -manifestSize);
                throw new ArgumentException($"Attempted to add duplicate manifest {hash.ToBase64()}.");
            }

            // At this point, the manifest is in memory and space is reserved.
            // Now, persist to disk.
            try
            {
                var fileName = hash.ToBase64().Replace('/', '_');
                var filePath = Path.Combine(ManifestDirectory, fileName);
                var manifestBytes = manifest.ToByteArray();
                File.WriteAllBytes(filePath, manifestBytes);

                if (rootPath is not null)
                {
                    File.WriteAllText($"{filePath}.meta", rootPath);
                }
                _logger.LogInformation("Added manifest {Hash} to store. Current size: {CurrentStoreSize} bytes.", hash.ToBase64(), newTotalSize);
            }
            catch (IOException ex)
            {
                // If persistence fails, we must roll back the in-memory state.
                _manifests.TryRemove(hash, out _);
                Interlocked.Add(ref _currentStoreSize, -manifestSize);
                _logger.LogError(ex, "Failed to write manifest {Hash} to disk. State has been rolled back.", hash.ToBase64());
                // Re-throw the critical exception
                throw;
            }
        }

        public SignedManifest? Get(ByteString hash)
        {
            return _manifests.TryGetValue(hash, out var value) ? value.Manifest : null;
        }

        private void LoadManifestsFromDisk()
        {
            _logger.LogInformation("Loading manifests from {ManifestDirectory}...", ManifestDirectory);
            long totalSize = 0;
            var manifestFiles = Directory.GetFiles(ManifestDirectory).Where(f => !f.EndsWith(".meta"));
            foreach (var file in manifestFiles)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    var manifestBytes = File.ReadAllBytes(file);
                    var manifest = SignedManifest.Parser.ParseFrom(manifestBytes);

                    var fileName = Path.GetFileName(file);
                    var hashBase64 = fileName.Replace('_', '/');
                    var hash = ByteString.FromBase64(hashBase64);

                    var calculatedHash = ByteString.CopyFrom(SHA256.HashData(manifest.Manifest.ToByteArray()));
                    if (!hash.Equals(calculatedHash))
                    {
                        _logger.LogWarning("Manifest file {fileName} is corrupt. Hash does not match content. Skipping.", fileName);
                        continue;
                    }

                    string? rootPath = null;
                    var metaFile = file + ".meta";
                    if (File.Exists(metaFile))
                    {
                        rootPath = File.ReadAllText(metaFile);
                    }

                    if (_manifests.TryAdd(hash, (manifest, rootPath)))
                    {
                        totalSize += fileInfo.Length;
                        _logger.LogInformation("Loaded manifest {Hash} from disk.", hash.ToBase64());
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load manifest from {file}.", file);
                }
            }
            _currentStoreSize = totalSize;
            _logger.LogInformation("Finished loading manifests. Total size: {CurrentStoreSize} bytes.", _currentStoreSize);
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
