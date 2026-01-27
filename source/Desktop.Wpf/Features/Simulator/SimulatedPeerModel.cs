using System;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatedPeerModel : IDisposable
{
    private DisposableBag _bag;
    private readonly ReactiveProperty<string?> _displayName;
    private readonly ReactiveProperty<bool> _isOnline;
    private readonly ReactiveProperty<bool> _isRelayCapable;
    private readonly ReactiveProperty<SimulatorPeerRuntimeState> _runtimeState;

    public SimulatedPeerModel(
        Guid peerId,
        string? displayName,
        bool isOnline,
        bool isRelayCapable,
        byte[] identitySigningKeySpki,
        byte[] identitySigningKeyPrivateKeyEcPrivateKey)
    {
        PeerId = peerId;

        IdentitySigningKeySpki = identitySigningKeySpki;
        IdentitySigningKeyPrivateKeyEcPrivateKey = identitySigningKeyPrivateKeyEcPrivateKey;

        _displayName = new ReactiveProperty<string?>(NormalizeDisplayName(displayName));
        _isOnline = new ReactiveProperty<bool>(isOnline);
        _isRelayCapable = new ReactiveProperty<bool>(isRelayCapable);

        _runtimeState = new ReactiveProperty<SimulatorPeerRuntimeState>(
            isOnline
                ? new SimulatorPeerRuntimeState { UiState = SimulatorPeerUiState.Ready }
                : new SimulatorPeerRuntimeState { UiState = SimulatorPeerUiState.Offline });
    }

    public Guid PeerId { get; }

    public byte[] IdentitySigningKeySpki { get; }
    internal byte[] IdentitySigningKeyPrivateKeyEcPrivateKey { get; }

    public ReadOnlyReactiveProperty<string?> DisplayName => _displayName;
    public ReadOnlyReactiveProperty<bool> IsOnline => _isOnline;
    public ReadOnlyReactiveProperty<bool> IsRelayCapable => _isRelayCapable;
    public ReadOnlyReactiveProperty<SimulatorPeerRuntimeState> RuntimeState => _runtimeState;

    internal void Track(IDisposable disposable)
        => disposable.AddTo(ref _bag);

    public void SetDisplayName(string? displayName)
        => _displayName.Value = NormalizeDisplayName(displayName);

    public void SetOnline(bool isOnline)
    {
        _isOnline.Value = isOnline;

        if (!isOnline)
        {
            _runtimeState.Value = new SimulatorPeerRuntimeState { UiState = SimulatorPeerUiState.Offline };
        }
        else
        {
            if (_runtimeState.Value.UiState == SimulatorPeerUiState.Offline)
            {
                _runtimeState.Value = new SimulatorPeerRuntimeState { UiState = SimulatorPeerUiState.Ready };
            }
        }
    }

    public void SetRelayCapable(bool isRelayCapable)
        => _isRelayCapable.Value = isRelayCapable;

    public void MarkOutboundPending(Guid requestCorrelationId)
        => _runtimeState.Value = new SimulatorPeerRuntimeState
        {
            UiState = SimulatorPeerUiState.OutboundPending,
            PendingCorrelationId = requestCorrelationId
        };

    public void MarkInboundPending(Guid requestCorrelationId)
        => _runtimeState.Value = new SimulatorPeerRuntimeState
        {
            UiState = SimulatorPeerUiState.InboundPending,
            PendingCorrelationId = requestCorrelationId
        };

    public void MarkEstablished()
        => _runtimeState.Value = new SimulatorPeerRuntimeState
        {
            UiState = SimulatorPeerUiState.Established,
            PendingCorrelationId = null
        };

    public void ClearRuntimeState()
        => _runtimeState.Value = _isOnline.Value
            ? new SimulatorPeerRuntimeState { UiState = SimulatorPeerUiState.Ready }
            : new SimulatorPeerRuntimeState { UiState = SimulatorPeerUiState.Offline };

    public void Dispose()
    {
        _bag.Dispose();
        _displayName.Dispose();
        _isOnline.Dispose();
        _isRelayCapable.Dispose();
        _runtimeState.Dispose();
    }

    private static string? NormalizeDisplayName(string? displayName)
        => string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
}
