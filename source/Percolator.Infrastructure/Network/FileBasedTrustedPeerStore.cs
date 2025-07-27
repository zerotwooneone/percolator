using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Percolator.Infrastructure.Serialization;
using Percolator.Network;

namespace Percolator.Infrastructure.Network;

public class FileBasedTrustedPeerStore : ITrustedPeerStore
{
    private ConcurrentDictionary<PublicKeyHash, byte> _trustedHashes;
    private readonly string _trustedHashesFilePath;
    private readonly PercolatorJsonContext _jsonContext;
    // Using a filename-based lock instead of a global Mutex for better testability
    private readonly object _fileLock = new object();

    public FileBasedTrustedPeerStore(IOptions<StorageOptions> storageOptions)
    {
        var dataDirectory = storageOptions.Value.Path;
        
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var percolatorAppDataPath = Path.Combine(appDataPath, dataDirectory);
        
        var storagePath = percolatorAppDataPath;
        _trustedHashesFilePath = Path.Combine(storagePath, "trusted_hashes.json");
        Directory.CreateDirectory(storagePath);
        _jsonContext = new PercolatorJsonContext(new JsonSerializerOptions { WriteIndented = true });
        
        _trustedHashes = LoadTrustedHashesFromFile();
    }

    public async Task AddAsync(PublicKeyHash publicKeyHash)
    {
        // Use a simple lock instead of a mutex for thread safety within the process
        lock (_fileLock)
        {
            var trustedHashes = LoadTrustedHashesFromFile(); 
            if (trustedHashes.TryAdd(publicKeyHash, 0))
            {
                _trustedHashes = trustedHashes;
                // Note: We're doing synchronous file I/O within a lock
                // This is acceptable for this specific use case as the file is small
                File.WriteAllText(_trustedHashesFilePath, 
                    JsonSerializer.Serialize(_trustedHashes.Keys.Select(h => h.Value).ToList()));
            }
        }
        
        // Maintain the async signature for API consistency
        await Task.CompletedTask;
    }

    public bool IsTrusted(PublicKeyHash publicKeyHash)
    {
        return _trustedHashes.ContainsKey(publicKeyHash);
    }
    
    public Task<IEnumerable<PublicKeyHash>> GetAllAsync()
    {
        return Task.FromResult<IEnumerable<PublicKeyHash>>(_trustedHashes.Keys);
    }

    private ConcurrentDictionary<PublicKeyHash, byte> LoadTrustedHashesFromFile()
    {
        if (!File.Exists(_trustedHashesFilePath))
        {
            return new ConcurrentDictionary<PublicKeyHash, byte>();
        }

        try
        {
            string json;
            // Open the file for reading, but allow other processes to also read 
            using (var stream = new FileStream(_trustedHashesFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(stream))
            {
                json = reader.ReadToEnd();
            }
            
            var hashes = JsonSerializer.Deserialize<List<byte[]>>(json) ?? new List<byte[]>();

            return new ConcurrentDictionary<PublicKeyHash, byte>(
                hashes.Select(h => new KeyValuePair<PublicKeyHash, byte>(new PublicKeyHash(h), 0)));
        }
        catch (Exception)
        {
            // In case of any serialization issues, start with a fresh dictionary
            return new ConcurrentDictionary<PublicKeyHash, byte>();
        }
    }

    // Removed SaveTrustedHashesToDiskAsync method as saving is now done synchronously in AddAsync
}
