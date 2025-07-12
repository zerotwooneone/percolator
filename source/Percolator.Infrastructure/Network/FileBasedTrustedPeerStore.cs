using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Percolator.Infrastructure.Identity;
using Percolator.Infrastructure.Serialization;
using Percolator.Network;

namespace Percolator.Infrastructure.Network;

public class FileBasedTrustedPeerStore : ITrustedPeerStore
{
    private readonly ConcurrentDictionary<PublicKeyHash, byte> _trustedHashes;
    private readonly string _trustedHashesFilePath;
    private readonly PercolatorJsonContext _jsonContext;
    private readonly SemaphoreSlim _semaphore;

    public FileBasedTrustedPeerStore(IOptions<StorageOptions> storageOptions)
    {
        var dataDirectory = storageOptions.Value.Path;
        
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var percolatorAppDataPath = Path.Combine(appDataPath, dataDirectory);
        
        var storagePath = percolatorAppDataPath;
        _trustedHashesFilePath = Path.Combine(storagePath, "trusted_hashes.json");
        Directory.CreateDirectory(storagePath);
        _jsonContext = new PercolatorJsonContext(new JsonSerializerOptions { WriteIndented = true });
        _semaphore = new SemaphoreSlim(1);

        _trustedHashes = LoadTrustedHashesFromFile();
    }

    public async Task AddAsync(PublicKeyHash publicKeyHash)
    {
        await _semaphore.WaitAsync();
        _trustedHashes.TryAdd(publicKeyHash, 0);
        await SaveTrustedHashesToDiskAsync();
        _semaphore.Release();
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

        var json = File.ReadAllText(_trustedHashesFilePath);
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
