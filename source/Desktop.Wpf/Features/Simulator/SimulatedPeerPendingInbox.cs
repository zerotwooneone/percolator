using System.Collections.Concurrent;
using Percolator.Contracts;
using Percolator.Network;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatedPeerPendingInbox
{
    void AddInviteHandshakeResponse(PeerId simulatedPeerId, Guid correlationId, InviteHandshakeResponse response);

    bool TryGetInviteHandshakeResponse(PeerId simulatedPeerId, Guid correlationId, out InviteHandshakeResponse response);

    bool TryTakeInviteHandshakeResponse(PeerId simulatedPeerId, Guid correlationId, out InviteHandshakeResponse response);

    IReadOnlyCollection<Guid> SnapshotInviteHandshakeResponseCorrelationIds(PeerId simulatedPeerId);
}

public sealed class SimulatedPeerPendingInbox : ISimulatedPeerPendingInbox
{
    private readonly ConcurrentDictionary<(PeerId PeerId, Guid CorrelationId), InviteHandshakeResponse> _inviteResponses = new();

    public void AddInviteHandshakeResponse(PeerId simulatedPeerId, Guid correlationId, InviteHandshakeResponse response)
    {
        if (response is null) throw new ArgumentNullException(nameof(response));
        _inviteResponses[(simulatedPeerId, correlationId)] = response;
    }

    public bool TryGetInviteHandshakeResponse(PeerId simulatedPeerId, Guid correlationId, out InviteHandshakeResponse response)
    {
        if (_inviteResponses.TryGetValue((simulatedPeerId, correlationId), out var existing))
        {
            response = existing;
            return true;
        }

        response = new InviteHandshakeResponse { Version = 1 };
        return false;
    }

    public bool TryTakeInviteHandshakeResponse(PeerId simulatedPeerId, Guid correlationId, out InviteHandshakeResponse response)
    {
        if (_inviteResponses.TryRemove((simulatedPeerId, correlationId), out var removed))
        {
            response = removed;
            return true;
        }

        response = new InviteHandshakeResponse { Version = 1 };
        return false;
    }

    public IReadOnlyCollection<Guid> SnapshotInviteHandshakeResponseCorrelationIds(PeerId simulatedPeerId)
    {
        var list = new List<Guid>();
        foreach (var kv in _inviteResponses)
        {
            if (kv.Key.PeerId == simulatedPeerId)
            {
                list.Add(kv.Key.CorrelationId);
            }
        }
        return list;
    }
}
