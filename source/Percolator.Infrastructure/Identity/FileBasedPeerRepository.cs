using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Percolator.Application.Identity;
using Percolator.Identity;
using Percolator.Infrastructure.Serialization;

namespace Percolator.Infrastructure.Identity;

public class FileBasedPeerRepository : IPeerRepository
{
    private readonly ConcurrentDictionary<PeerId, Peer> _peers;
    private readonly string _filePath;
    private readonly PercolatorJsonContext _jsonContext;

    public FileBasedPeerRepository(IOptions<StorageOptions> storageOptions)
    {
        var storagePath = storageOptions.Value.Path;
        _filePath = Path.Combine(storagePath, "peers.json");
        _jsonContext = new PercolatorJsonContext(new JsonSerializerOptions { WriteIndented = true });

        _peers = LoadPeersFromFile();
    }

    public Task<Peer?> GetByIdAsync(PeerId id) => Task.FromResult(_peers.GetValueOrDefault(id));

    public Task<Peer?> GetByThumbprintAsync(string thumbprint) => Task.FromResult(_peers.Values.FirstOrDefault(p => p.Thumbprint == thumbprint));

    public async Task AddAsync(Peer peer)
    {
        _peers[peer.Id] = peer;
        await SaveChangesToDiskAsync();
    }

    public async Task RemoveAsync(PeerId id)
    {
        if (_peers.TryRemove(id, out _))
        {
            await SaveChangesToDiskAsync();
        }
    }

    private ConcurrentDictionary<PeerId, Peer> LoadPeersFromFile()
    {
        if (!File.Exists(_filePath))
        {
            return new ConcurrentDictionary<PeerId, Peer>();
        }

        var json = File.ReadAllText(_filePath);
        var models = JsonSerializer.Deserialize(json, _jsonContext.ListPeerModel) ?? new List<PeerModel>();

        return new ConcurrentDictionary<PeerId, Peer>(models.Select(ToDomain).ToDictionary(p => p.Id));
    }

    private async Task SaveChangesToDiskAsync()
    {
        var models = _peers.Values.Select(ToModel).ToList();
        var json = JsonSerializer.Serialize(models, _jsonContext.ListPeerModel);
        await File.WriteAllTextAsync(_filePath, json);
    }

    private static Peer ToDomain(PeerModel model) =>
        new(new PeerId(model.Id), model.IpAddress, new Endpoint(model.GrpcEndpoint.Port), model.Thumbprint);

    private static PeerModel ToModel(Peer peer) =>
        new()
        {
            Id = peer.Id.Value,
            IpAddress = peer.IpAddress,
            GrpcEndpoint = new EndpointModel { Port = peer.GrpcEndpoint.Port },
            Thumbprint = peer.Thumbprint
        };
}
