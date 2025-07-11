using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Percolator.Identity;
using Percolator.Infrastructure.Serialization;
using Percolator.Network;
using Peer = Percolator.Identity.Peer;
using IdentityPeerId = Percolator.Identity.PeerId;

namespace Percolator.Infrastructure.Identity;

public class FileBasedPeerRepository : IPeerRepository, ITrustedPeerStore
{
    private readonly ConcurrentDictionary<IdentityPeerId, PeerModel> _peers;
    private readonly ConcurrentDictionary<PublicKeyHash, byte> _trustedHashes;
    private readonly string _peersFilePath;
    private readonly string _trustedHashesFilePath;
    private readonly PercolatorJsonContext _jsonContext;
    private readonly SemaphoreSlim _semaphore;
    private readonly StorageOptions _storageOptions;

    public FileBasedPeerRepository(IOptions<StorageOptions> storageOptions)
    {
        _storageOptions = storageOptions.Value;
        var storagePath = _storageOptions.Path;
        _peersFilePath = Path.Combine(storagePath, "peers.json");
        _trustedHashesFilePath = Path.Combine(storagePath, "trusted_hashes.json");
        _jsonContext = new PercolatorJsonContext(new JsonSerializerOptions { WriteIndented = true });
        _semaphore = new SemaphoreSlim(1);

        _peers = LoadPeersFromFile();
        _trustedHashes = LoadTrustedHashesFromFile();
    }

    public Task<Peer?> GetByIdAsync(IdentityPeerId id)
    {
        var model = _peers.TryGetValue(id, out var peerModel) ? ToDomain(peerModel) : null;
        return Task.FromResult(model);
    }

    public async Task AddAsync(Peer peer)
    {
        var model = ToModel(peer);
        await _semaphore.WaitAsync();
        _peers[peer.Id] = model;
        await SavePeersToDiskAsync();
        _semaphore.Release();
    }

    public async Task RemoveAsync(IdentityPeerId id)
    {
        if (_peers.TryRemove(id, out _))
        {
            await SavePeersToDiskAsync();
        }
    }

    public async Task<Peer?> GetByNameAsync(string name)
    {
        var peerFiles = Directory.GetFiles(Path.Combine(_storageOptions.Path, "peers"), "*.json");
        foreach (var file in peerFiles)
        {
            var json = await File.ReadAllTextAsync(file);
            var model = JsonSerializer.Deserialize<PeerModel>(json, _jsonContext.PeerModel);
            if (model?.Name.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
            {
                return ToDomain(model);
            }
        }
        return null;
    }

    private ConcurrentDictionary<IdentityPeerId, PeerModel> LoadPeersFromFile()
    {
        if (!File.Exists(_peersFilePath))
        {
            return new ConcurrentDictionary<IdentityPeerId, PeerModel>();
        }

        var json = File.ReadAllText(_peersFilePath);
        var models = JsonSerializer.Deserialize(json, _jsonContext.ListPeerModel) ?? new List<PeerModel>();

        return new ConcurrentDictionary<IdentityPeerId, PeerModel>(models.ToDictionary(p => new IdentityPeerId(p.Id)));
    }

    private async Task SavePeersToDiskAsync()
    {
        var models = _peers.Values.ToList();
        var json = JsonSerializer.Serialize(models, _jsonContext.ListPeerModel);
        await File.WriteAllTextAsync(_peersFilePath, json);
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
        var hashes = _trustedHashes.Keys.Select(k => k.Value).ToList();
        var json = JsonSerializer.Serialize(hashes);
        await File.WriteAllTextAsync(_trustedHashesFilePath, json);
    }

    private Peer ToDomain(PeerModel model) =>
        new(new IdentityPeerId(model.Id), model.Name);

    private PeerModel ToModel(Peer peer) =>
        new()
        {
            Id = peer.Id.Value,
            Name = peer.Name,
            PublicKey = null
        };

    public async Task AddAsync(PublicKeyHash publicKeyHash)
    {
        if (_trustedHashes.TryAdd(publicKeyHash, 0))
        {
            await SaveTrustedHashesToDiskAsync();
        }
    }

    public bool IsTrusted(PublicKeyHash publicKeyHash)
    {
        return _trustedHashes.ContainsKey(publicKeyHash);
    }
}
