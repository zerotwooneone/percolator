using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Percolator.Identity;
using Percolator.Infrastructure.Serialization;
using Peer = Percolator.Identity.Peer;
using IdentityPeerId = Percolator.Identity.PeerId;

namespace Percolator.Infrastructure.Identity;

public class FileBasedPeerRepository : IPeerRepository
{
    private readonly ConcurrentDictionary<IdentityPeerId, PeerModel> _peers;
    private readonly string _peersFilePath;
    private readonly PercolatorJsonContext _jsonContext;
    private readonly SemaphoreSlim _semaphore;

    public FileBasedPeerRepository(IOptions<StorageOptions> storageOptions)
    {
        var dataDirectory = storageOptions.Value.Path;
        
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var percolatorAppDataPath = Path.Combine(appDataPath, dataDirectory);
        
        var storagePath = percolatorAppDataPath;
        _peersFilePath = Path.Combine(storagePath, "peers.json");
        Directory.CreateDirectory(storagePath);
        _jsonContext = new PercolatorJsonContext(new JsonSerializerOptions { WriteIndented = true });
        _semaphore = new SemaphoreSlim(1);

        _peers = LoadPeersFromFile();
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

    public Task<Peer?> GetByNameAsync(string name)
    {
        var model = _peers.Values.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(model is not null ? ToDomain(model) : null);
    }

    public Task<IEnumerable<Peer>> GetAllAsync()
    {
        var peers = _peers.Values.Select(ToDomain);
        return Task.FromResult(peers);
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

    private static Peer ToDomain(PeerModel model)
    {
        return new Peer(new IdentityPeerId(model.Id), model.Name);
    }

    private static PeerModel ToModel(Peer peer)
    {
        return new PeerModel { Id = peer.Id.Value, Name = peer.Name };
    }
}
