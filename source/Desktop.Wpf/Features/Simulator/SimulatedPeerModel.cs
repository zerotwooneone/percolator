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
        byte[] identitySigningKeyPrivateKeyEcPrivateKey,
        SimulatorPeerRuntimeState? initialRuntimeState = null)
    {
        PeerId = peerId;

        IdentitySigningKeySpki = identitySigningKeySpki;
        IdentitySigningKeyPrivateKeyEcPrivateKey = identitySigningKeyPrivateKeyEcPrivateKey;

        _displayName = new ReactiveProperty<string?>(NormalizeDisplayName(displayName));
        _isOnline = new ReactiveProperty<bool>(isOnline);
        _isRelayCapable = new ReactiveProperty<bool>(isRelayCapable);

        var state = initialRuntimeState ?? new SimulatorPeerRuntimeState { UiState = SimulatorPeerUiState.Ready };
        if (!isOnline)
        {
            state = state with { UiState = SimulatorPeerUiState.Offline };
        }
        else if (state.UiState == SimulatorPeerUiState.Offline)
        {
            state = state with { UiState = SimulatorPeerUiState.Ready, PendingCorrelationId = null };
        }

        _runtimeState = new ReactiveProperty<SimulatorPeerRuntimeState>(state);
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
            _runtimeState.Value = _runtimeState.Value with { UiState = SimulatorPeerUiState.Offline };
        }
        else
        {
            if (_runtimeState.Value.UiState == SimulatorPeerUiState.Offline)
            {
                _runtimeState.Value = _runtimeState.Value with { UiState = SimulatorPeerUiState.Ready, PendingCorrelationId = null };
            }
        }
    }

    public void SetRelayCapable(bool isRelayCapable)
        => _isRelayCapable.Value = isRelayCapable;

    public void MarkOutboundPending(Guid requestCorrelationId)
        => _runtimeState.Value = UpsertAttempt(_runtimeState.Value, requestCorrelationId) with
        {
            UiState = SimulatorPeerUiState.OutboundPending,
            PendingCorrelationId = requestCorrelationId
        };

    public void MarkInboundPending(Guid requestCorrelationId)
        => _runtimeState.Value = UpsertAttempt(_runtimeState.Value, requestCorrelationId) with
        {
            UiState = SimulatorPeerUiState.InboundPending,
            PendingCorrelationId = requestCorrelationId
        };

    public void MarkEstablished()
        => _runtimeState.Value = _runtimeState.Value with
        {
            UiState = SimulatorPeerUiState.Established,
            PendingCorrelationId = null,
            Phase = null,
            NotUntilUtc = null,
            LastError = null
        };

    public void MarkExpired()
        => _runtimeState.Value = _runtimeState.Value with
        {
            UiState = SimulatorPeerUiState.Expired,
            PendingCorrelationId = null,
            Phase = null
        };

    public void ClearRuntimeState()
        => _runtimeState.Value = _runtimeState.Value with
        {
            UiState = _isOnline.Value ? SimulatorPeerUiState.Ready : SimulatorPeerUiState.Offline,
            PendingCorrelationId = null,
            TargetPublicKeyHash = null,
            SelectedRouteMode = null,
            DirectEndpoint = null,
            RelayHostPeerId = null,
            Phase = null,
            NotUntilUtc = null,
            LastError = null,
            HandshakeAttempts = new()
        };

    private static SimulatorPeerRuntimeState UpsertAttempt(SimulatorPeerRuntimeState state, Guid correlationId)
    {
        var attempts = state.HandshakeAttempts ?? new();

        var idx = attempts.FindIndex(a => a.CorrelationId == correlationId);
        var createdAt = idx >= 0 ? attempts[idx].CreatedAtUtc : DateTimeOffset.UtcNow;
        var snapshot = new SimulatorHandshakeAttemptState
        {
            CorrelationId = correlationId,
            TargetPublicKeyHash = state.TargetPublicKeyHash,
            SelectedRouteMode = state.SelectedRouteMode,
            DirectEndpoint = state.DirectEndpoint,
            RelayHostPeerId = state.RelayHostPeerId,
            Phase = state.Phase,
            NotUntilUtc = state.NotUntilUtc,
            LastError = state.LastError,
            CreatedAtUtc = createdAt
        };

        if (idx >= 0) attempts[idx] = snapshot;
        else attempts.Add(snapshot);

        return state with { HandshakeAttempts = attempts };
    }

    public void SetAttemptPhase(Guid correlationId, string? phase)
        => _runtimeState.Value = UpdateAttempt(_runtimeState.Value, correlationId, a => a with { Phase = phase });

    public void SetAttemptError(Guid correlationId, string? error)
        => _runtimeState.Value = UpdateAttempt(_runtimeState.Value, correlationId, a => a with { LastError = error });

    public void SetAttemptNotUntil(Guid correlationId, DateTimeOffset? notUntilUtc)
        => _runtimeState.Value = UpdateAttempt(_runtimeState.Value, correlationId, a => a with { NotUntilUtc = notUntilUtc });

    private static SimulatorPeerRuntimeState UpdateAttempt(
        SimulatorPeerRuntimeState state,
        Guid correlationId,
        Func<SimulatorHandshakeAttemptState, SimulatorHandshakeAttemptState> update)
    {
        var attempts = state.HandshakeAttempts ?? new();
        var idx = attempts.FindIndex(a => a.CorrelationId == correlationId);
        if (idx < 0)
        {
            // Create a new attempt snapshot from current summary fields, then apply the update.
            var initial = new SimulatorHandshakeAttemptState
            {
                CorrelationId = correlationId,
                TargetPublicKeyHash = state.TargetPublicKeyHash,
                SelectedRouteMode = state.SelectedRouteMode,
                DirectEndpoint = state.DirectEndpoint,
                RelayHostPeerId = state.RelayHostPeerId,
                Phase = state.Phase,
                NotUntilUtc = state.NotUntilUtc,
                LastError = state.LastError,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            attempts.Add(update(initial));
            return state with { HandshakeAttempts = attempts };
        }

        attempts[idx] = update(attempts[idx]);
        return state with { HandshakeAttempts = attempts };
    }

    public void SetRuntimeState(SimulatorPeerRuntimeState state)
    {
        if (!IsOnline.CurrentValue)
        {
            _runtimeState.Value = state with { UiState = SimulatorPeerUiState.Offline };
            return;
        }

        if (state.UiState == SimulatorPeerUiState.Offline)
        {
            _runtimeState.Value = state with { UiState = SimulatorPeerUiState.Ready, PendingCorrelationId = null };
            return;
        }

        _runtimeState.Value = state;
    }

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
