using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Percolator.Infrastructure.Serialization;
using Percolator.Network;

namespace Percolator.Infrastructure.Network;

public class FileBasedPeerConnectionRepository : IPeerConnectionRepository
{
    private readonly string _filePath;
    private readonly ConcurrentDictionary<PeerId, PeerConnection> _connections = new();
    private readonly JsonSerializerOptions _jsonSerializerOptions;

    public FileBasedPeerConnectionRepository(IOptions<StorageOptions> options)
    {
        var dataDirectory = options.Value.Path;
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "peer_connections.json");
        _jsonSerializerOptions = new JsonSerializerOptions { WriteIndented = true };
        LoadConnections();
    }

    public Task<PeerConnection?> GetByIdAsync(PeerId peerId)
    {
        _connections.TryGetValue(peerId, out var connection);
        return Task.FromResult(connection);
    }

    public async Task SaveAsync(PeerConnection peerConnection)
    {
        _connections[peerConnection.Id] = peerConnection;
        await PersistConnections();
    }

    public Task<PeerConnection?> GetByDirectMessage(DirectMessagePublicKey directMessagePublicKey)
    {
        var connection = _connections.Values.FirstOrDefault(c => c.IdentitySigningKey == directMessagePublicKey);
        return Task.FromResult(connection);
    }

    public Task<PeerConnection?> GetByTlsCertificateAsync(TlsCertificate tlsCertificate)
    {
        var connection = _connections.Values.FirstOrDefault(c =>
            c.TlsCertificates.Any(storedCert => storedCert.RawData.SequenceEqual(tlsCertificate.RawData)));
        return Task.FromResult(connection);
    }

    public async Task UpdateDirectMessagePublicKeyAsync(PeerId peerId, DirectMessagePublicKey publicKey)
    {
        if (!_connections.TryGetValue(peerId, out var existingConnection))
        {
            throw new KeyNotFoundException($"No peer connection found for PeerId: {peerId.Value}");
        }

        // PeerConnection has init-only properties, so we must create a new instance.
        var updatedConnection = new PeerConnection(
            existingConnection.Id,
            publicKey, // The new public key
            existingConnection.GrpcEndPoints,
            existingConnection.TlsCertificates,
            existingConnection.LastSeen);

        _connections[peerId] = updatedConnection;
        await PersistConnections();
    }

    private void LoadConnections()
    {
        if (!File.Exists(_filePath)) return;

        var json = File.ReadAllText(_filePath);
        var models = JsonSerializer.Deserialize<List<PeerConnectionModel>>(json, _jsonSerializerOptions);
        if (models is null) return;

        foreach (var model in models)
        {
            _connections[new PeerId(model.Id)] = ToDomain(model);
        }
    }

    private async Task PersistConnections()
    {
        var models = _connections.Values.Select(ToModel).ToList();
        var json = JsonSerializer.Serialize(models, _jsonSerializerOptions);
        await File.WriteAllTextAsync(_filePath, json);
    }

    private static PeerConnection ToDomain(PeerConnectionModel model) =>
        new(new PeerId(model.Id),
            model.DirectMessagePublicKey is null ? null : new DirectMessagePublicKey(model.DirectMessagePublicKey),
            model.GrpcEndPoints.Select(e => new GrpcEndPoint(new DnsEndPoint(e.Host, e.Port), e.LastSeen)).ToList(),
            model.TlsCertificates.Select(c => new TlsCertificate(c.RawData)).ToList(),
            model.LastSeen);

    private static PeerConnectionModel ToModel(PeerConnection connection) =>
        new()
        {
            Id = connection.Id.Value,
            DirectMessagePublicKey = connection.IdentitySigningKey?.Value,
            GrpcEndPoints = connection.GrpcEndPoints.Select(e => new GrpcEndPointModel { Host = e.EndPoint.Host, Port = e.EndPoint.Port, LastSeen = e.LastSeen }).ToList(),
            TlsCertificates = connection.TlsCertificates.Select(c => new TlsCertificateModel { RawData = c.RawData }).ToList(),
            LastSeen = connection.LastSeen
        };
}
