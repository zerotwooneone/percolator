using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Percolator.Application.Identity;
using Percolator.Identity;
using Percolator.Infrastructure.Serialization;
using Percolator.Network;
using Peer = Percolator.Identity.Peer;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Infrastructure.Identity;

public class FileBasedPeerRepository : IPeerRepository, ITrustedPeerStore
{
    private readonly ConcurrentDictionary<PeerId, Peer> _peers;
    private readonly ConcurrentDictionary<PublicKeyHash, byte> _trustedHashes;
    private readonly string _peersFilePath;
    private readonly string _trustedHashesFilePath;
    private readonly PercolatorJsonContext _jsonContext;

    public FileBasedPeerRepository(IOptions<StorageOptions> storageOptions)
    {
        var storagePath = storageOptions.Value.Path;
        _peersFilePath = Path.Combine(storagePath, "peers.json");
        _trustedHashesFilePath = Path.Combine(storagePath, "trusted_hashes.json");
        _jsonContext = new PercolatorJsonContext(new JsonSerializerOptions { WriteIndented = true });

        _peers = LoadPeersFromFile();
        _trustedHashes = LoadTrustedHashesFromFile();
    }

    public Task<Peer?> GetByIdAsync(PeerId id) => Task.FromResult(_peers.GetValueOrDefault(id));

    public Task<Peer?> GetByThumbprintAsync(string thumbprint) => Task.FromResult(_peers.Values.FirstOrDefault(p => p.Thumbprint == thumbprint));

    public async Task AddAsync(Peer peer)
    {
        _peers[peer.Id] = peer;
        await SavePeersToDiskAsync();
    }

    public async Task RemoveAsync(PeerId id)
    {
        if (_peers.TryRemove(id, out _))
        {
            await SavePeersToDiskAsync();
        }
    }

    private ConcurrentDictionary<PeerId, Peer> LoadPeersFromFile()
    {
        if (!File.Exists(_peersFilePath))
        {
            return new ConcurrentDictionary<PeerId, Peer>();
        }

        var json = File.ReadAllText(_peersFilePath);
        var models = JsonSerializer.Deserialize(json, _jsonContext.ListPeerModel) ?? new List<PeerModel>();

        return new ConcurrentDictionary<PeerId, Peer>(models.Select(ToDomain).ToDictionary(p => p.Id));
    }

    private async Task SavePeersToDiskAsync()
    {
        var models = _peers.Values.Select(ToModel).ToList();
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

    private static Peer ToDomain(PeerModel model) =>
        new(new PeerId(model.Id), model.IpAddress, new Endpoint(model.GrpcEndpoint.Port), model.Thumbprint, model.LastDirectConversationId is null ? null : new ConversationId(model.LastDirectConversationId.Value));

    private static PeerModel ToModel(Peer peer) =>
        new()
        {
            Id = peer.Id.Value,
            IpAddress = peer.IpAddress,
            GrpcEndpoint = new EndpointModel { Port = peer.GrpcEndpoint.Port },
            Thumbprint = peer.Thumbprint,
            LastDirectConversationId = peer.LastDirectConversationId?.Value
        };

    public async void Add(PublicKeyHash publicKeyHash)
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
