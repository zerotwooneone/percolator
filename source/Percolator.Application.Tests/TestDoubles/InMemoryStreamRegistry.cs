using System.Threading.Channels;
using Percolator.Application.Ports;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Tests.TestDoubles;

public sealed class InMemoryStreamRegistry : IStreamRegistryManagement
{
    private readonly HashSet<PublicIdentityId> _activeStreams = [];
    private readonly HashSet<PublicIdentityId> _activeRelayStreams = [];
    private readonly HashSet<PublicIdentityId> _backpressuredStreams = [];
    private readonly Dictionary<PublicIdentityId, Uri> _directEndpoints = [];
    public List<(PublicIdentityId TargetId, byte[] Payload)> WrittenPayloads { get; } = [];

    public void SetStreamActive(PublicIdentityId targetId, bool active = true)
    {
        if (active) _activeStreams.Add(targetId);
        else _activeStreams.Remove(targetId);
    }

    public void SetRelayStreamActive(PublicIdentityId relayId, bool active = true)
    {
        if (active) _activeRelayStreams.Add(relayId);
        else _activeRelayStreams.Remove(relayId);
    }

    public void SetBackpressured(PublicIdentityId targetId, bool backpressured = true)
    {
        if (backpressured) _backpressuredStreams.Add(targetId);
        else _backpressuredStreams.Remove(targetId);
    }

    public bool HasActiveStream(PublicIdentityId targetId) => _activeStreams.Contains(targetId);
    public bool HasActiveRelayStream(PublicIdentityId relayId) => _activeRelayStreams.Contains(relayId);

    public ValueTask<StreamWriteResult> TryWriteAsync(
        PublicIdentityId targetId,
        ReadOnlyMemory<byte> framedPayload,
        CancellationToken ct = default)
    {
        if (!_activeStreams.Contains(targetId))
        {
            return ValueTask.FromResult(StreamWriteResult.Closed("Stream is offline."));
        }

        if (_backpressuredStreams.Contains(targetId))
        {
            return ValueTask.FromResult(StreamWriteResult.Backpressured("Stream buffer full."));
        }

        WrittenPayloads.Add((targetId, framedPayload.ToArray()));
        return ValueTask.FromResult(StreamWriteResult.Success());
    }

    public void RegisterDirectEndpoint(PublicIdentityId targetId, Uri directEndpoint) => _directEndpoints[targetId] = directEndpoint;
    public void UnregisterDirectEndpoint(PublicIdentityId targetId) => _directEndpoints.Remove(targetId);
    public Uri? GetDirectEndpoint(PublicIdentityId targetId) => _directEndpoints.TryGetValue(targetId, out var uri) ? uri : null;
    public ChannelReader<ReadOnlyMemory<byte>> RegisterStream(PublicIdentityId targetId, int? capacity = null) => throw new NotSupportedException();
    public ChannelReader<ReadOnlyMemory<byte>> RegisterRelayStream(PublicIdentityId relayId, int? capacity = null) => throw new NotSupportedException();
    public void UnregisterStream(PublicIdentityId targetId) => _activeStreams.Remove(targetId);
    public void UnregisterRelayStream(PublicIdentityId relayId) => _activeRelayStreams.Remove(relayId);
}
