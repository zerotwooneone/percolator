using Percolator.Application2.Ports;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Tests.TestDoubles;

public sealed class InMemoryStreamRegistry : IStreamRegistry
{
    private readonly HashSet<PublicIdentityId> _activeStreams = [];
    private readonly HashSet<PublicIdentityId> _activeRelayStreams = [];
    private readonly HashSet<PublicIdentityId> _backpressuredStreams = [];
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
}
