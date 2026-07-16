using Grpc.Core;
using Percolator.Contracts;
using Percolator.Identity;
using System.Collections.Concurrent;

namespace Percolator.Application.Network.RelayHost;

/// <summary>
/// Manages active relay host streaming connections.
/// Thread-safe singleton that tracks connected clients and their response streams.
/// </summary>
public sealed class RelayHostStreamManager
{
    private readonly ConcurrentDictionary<PublicIdentityId, IServerStreamWriter<ServerRelayStream>> _activeStreams = new();

    /// <summary>
    /// Register a client's stream for message delivery.
    /// </summary>
    public void RegisterClient(PublicIdentityId clientId, IServerStreamWriter<ServerRelayStream> responseStream)
    {
        _activeStreams.AddOrUpdate(clientId, responseStream, (_, existing) => responseStream);
    }

    /// <summary>
    /// Remove a client's stream (typically on disconnect or certificate expiry).
    /// </summary>
    public void RemoveClient(PublicIdentityId clientId)
    {
        _activeStreams.TryRemove(clientId, out _);
    }

    /// <summary>
    /// Check if a client is currently connected.
    /// </summary>
    public bool IsClientConnected(PublicIdentityId clientId)
    {
        return _activeStreams.ContainsKey(clientId);
    }

    /// <summary>
    /// Get the active stream for a client (if connected).
    /// </summary>
    public bool TryGetStream(PublicIdentityId clientId, out IServerStreamWriter<ServerRelayStream>? stream)
    {
        return _activeStreams.TryGetValue(clientId, out stream);
    }
}
