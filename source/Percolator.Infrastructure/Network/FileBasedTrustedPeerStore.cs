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
    private static readonly Mutex _mutex = new Mutex(false, "Global\\PercolatorTrustedPeerStoreMutex");

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
        _mutex.WaitOne();
        _trustedHashes.TryAdd(publicKeyHash, 0);
        
        try
        {
            // wait if another process is already using it.
            _mutex.WaitOne();

            // --- CRITICAL SECTION START ---
            // Once we get here, no other process can enter until we release the mutex.
            
            var trustedHashes = LoadTrustedHashesFromFile(); 
            if (trustedHashes.TryAdd(publicKeyHash, 0))
            {
                _trustedHashes = trustedHashes;
                await SaveTrustedHashesToDiskAsync();
            }
        }
        finally
        {
            // IMPORTANT: Always release so other
            // processes can have their turn.
            _mutex.ReleaseMutex();
        }
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

        string json;
        // Open the file for reading, but allow other processes to also read 
        using (var stream = new FileStream(_trustedHashesFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using (var reader = new StreamReader(stream))
            {
                json = reader.ReadToEnd();
            }
        }
        var hashes = JsonSerializer.Deserialize<List<byte[]>>(json) ?? new List<byte[]>();

        return new ConcurrentDictionary<PublicKeyHash, byte>(
            hashes.Select(h => new KeyValuePair<PublicKeyHash, byte>(new PublicKeyHash(h), 0)));
    }

    private async Task SaveTrustedHashesToDiskAsync()
    {
        var hashes = _trustedHashes.Keys.Select(h => h.Value).ToList();
        var json = JsonSerializer.Serialize(hashes);
        await File.WriteAllTextAsync(_trustedHashesFilePath, json);
    }
}
