using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Percolator.Contracts;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatedPeerPendingInbox
{
    void AddInviteHandshakeResponse(Guid simulatedPeerId, Guid correlationId, InviteHandshakeResponse response);

    bool TryTakeInviteHandshakeResponse(Guid simulatedPeerId, Guid correlationId, out InviteHandshakeResponse response);

    IReadOnlyCollection<Guid> SnapshotInviteHandshakeResponseCorrelationIds(Guid simulatedPeerId);
}

public sealed class SimulatedPeerPendingInbox : ISimulatedPeerPendingInbox
{
    private readonly ConcurrentDictionary<(Guid PeerId, Guid CorrelationId), InviteHandshakeResponse> _inviteResponses = new();

    public void AddInviteHandshakeResponse(Guid simulatedPeerId, Guid correlationId, InviteHandshakeResponse response)
    {
        if (response is null) throw new ArgumentNullException(nameof(response));
        _inviteResponses[(simulatedPeerId, correlationId)] = response;
    }

    public bool TryTakeInviteHandshakeResponse(Guid simulatedPeerId, Guid correlationId, out InviteHandshakeResponse response)
    {
        if (_inviteResponses.TryRemove((simulatedPeerId, correlationId), out var removed))
        {
            response = removed;
            return true;
        }

        response = new InviteHandshakeResponse { Version = 1 };
        return false;
    }

    public IReadOnlyCollection<Guid> SnapshotInviteHandshakeResponseCorrelationIds(Guid simulatedPeerId)
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
