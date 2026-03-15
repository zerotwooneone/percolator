using System.Collections.Concurrent;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorRuntimeStateService
{
    SimulatorPeerRuntimeState Get(Guid peerId, bool isOnline);

    void MarkOutboundPending(Guid peerId, Guid requestCorrelationId);
    void MarkInboundPending(Guid peerId, Guid requestCorrelationId);
    void MarkEstablished(Guid peerId);
    void MarkExpired(Guid peerId);
    void Clear(Guid peerId);
}

public sealed class SimulatorRuntimeStateService : ISimulatorRuntimeStateService
{
    private readonly ConcurrentDictionary<Guid, SimulatorPeerRuntimeState> _runtime = new();

    public SimulatorPeerRuntimeState Get(Guid peerId, bool isOnline)
    {
        if (!isOnline)
        {
            return new SimulatorPeerRuntimeState { UiState = SimulatorPeerUiState.Offline };
        }

        if (_runtime.TryGetValue(peerId, out var state))
        {
            if (state.UiState == SimulatorPeerUiState.Offline)
            {
                return state with { UiState = SimulatorPeerUiState.Ready, PendingCorrelationId = null };
            }

            return state;
        }

        return new SimulatorPeerRuntimeState { UiState = SimulatorPeerUiState.Ready };
    }

    public void MarkOutboundPending(Guid peerId, Guid requestCorrelationId)
    {
        _runtime[peerId] = new SimulatorPeerRuntimeState
        {
            UiState = SimulatorPeerUiState.OutboundPending,
            PendingCorrelationId = requestCorrelationId
        };
    }

    public void MarkInboundPending(Guid peerId, Guid requestCorrelationId)
    {
        _runtime[peerId] = new SimulatorPeerRuntimeState
        {
            UiState = SimulatorPeerUiState.InboundPending,
            PendingCorrelationId = requestCorrelationId
        };
    }

    public void MarkEstablished(Guid peerId)
    {
        _runtime[peerId] = new SimulatorPeerRuntimeState
        {
            UiState = SimulatorPeerUiState.Established,
            PendingCorrelationId = null
        };
    }

    public void MarkExpired(Guid peerId)
    {
        _runtime[peerId] = new SimulatorPeerRuntimeState
        {
            UiState = SimulatorPeerUiState.Expired,
            PendingCorrelationId = null
        };
    }

    public void Clear(Guid peerId)
    {
        _runtime.TryRemove(peerId, out _);
    }
}
