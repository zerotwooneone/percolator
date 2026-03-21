using R3;
using Desktop.Wpf.Shared.Models;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatedPeerModel : IDisposable
{
    private DisposableBag _bag;
    private readonly ReactiveProperty<string?> _displayName;
    private readonly ReactiveProperty<bool> _isOnline;
    private readonly ReactiveProperty<bool> _isRelayCapable;

    private readonly ReactiveProperty<SimulatorPeerUiState> _uiState;
    private readonly ReactiveProperty<Guid?> _pendingCorrelationId;
    private readonly ReactiveProperty<byte[]?> _targetPublicKeyHash;
    private readonly ReactiveProperty<ConnectionMode?> _selectedRouteMode;
    private readonly ReactiveProperty<string?> _directEndpoint;
    private readonly ReactiveProperty<Guid?> _relayHostPeerId;
    private readonly ReactiveProperty<string?> _phase;
    private readonly ReactiveProperty<DateTimeOffset?> _notUntilUtc;
    private readonly ReactiveProperty<string?> _lastError;

    private readonly ModelList<SimulatorHandshakeAttemptState> _handshakeAttempts;

    public SimulatedPeerModel(
        Guid peerId,
        string? displayName,
        bool isOnline,
        bool isRelayCapable,
        byte[] identitySigningKeySpki,
        byte[] identitySigningKeyPrivateKeyEcPrivateKey,
        SimulatorPeerUiState uiState = SimulatorPeerUiState.Ready,
        Guid? pendingCorrelationId = null,
        byte[]? targetPublicKeyHash = null,
        ConnectionMode? selectedRouteMode = null,
        string? directEndpoint = null,
        Guid? relayHostPeerId = null,
        string? phase = null,
        DateTimeOffset? notUntilUtc = null,
        string? lastError = null,
        List<SimulatorHandshakeAttemptState>? handshakeAttempts = null)
    {
        PeerId = peerId;

        IdentitySigningKeySpki = identitySigningKeySpki;
        IdentitySigningKeyPrivateKeyEcPrivateKey = identitySigningKeyPrivateKeyEcPrivateKey;

        _displayName = new ReactiveProperty<string?>(NormalizeDisplayName(displayName));
        _isOnline = new ReactiveProperty<bool>(isOnline);
        _isRelayCapable = new ReactiveProperty<bool>(isRelayCapable);

        var ui = uiState;
        var pending = pendingCorrelationId;
        if (!isOnline)
        {
            ui = SimulatorPeerUiState.Offline;
        }
        else if (ui == SimulatorPeerUiState.Offline)
        {
            ui = SimulatorPeerUiState.Ready;
            pending = null;
        }

        _uiState = new ReactiveProperty<SimulatorPeerUiState>(ui);
        _pendingCorrelationId = new ReactiveProperty<Guid?>(pending);
        _targetPublicKeyHash = new ReactiveProperty<byte[]?>(targetPublicKeyHash);
        _selectedRouteMode = new ReactiveProperty<ConnectionMode?>(selectedRouteMode);
        _directEndpoint = new ReactiveProperty<string?>(directEndpoint);
        _relayHostPeerId = new ReactiveProperty<Guid?>(relayHostPeerId);
        _phase = new ReactiveProperty<string?>(phase);
        _notUntilUtc = new ReactiveProperty<DateTimeOffset?>(notUntilUtc);
        _lastError = new ReactiveProperty<string?>(lastError);

        _handshakeAttempts = new ModelList<SimulatorHandshakeAttemptState>();
        _handshakeAttempts.Reset(handshakeAttempts ?? new());
    }

    public Guid PeerId { get; }

    public byte[] IdentitySigningKeySpki { get; }
    internal byte[] IdentitySigningKeyPrivateKeyEcPrivateKey { get; }

    public ReadOnlyReactiveProperty<string?> DisplayName => _displayName;
    public ReadOnlyReactiveProperty<bool> IsOnline => _isOnline;
    public ReadOnlyReactiveProperty<bool> IsRelayCapable => _isRelayCapable;

    public ReadOnlyReactiveProperty<SimulatorPeerUiState> UiState => _uiState;
    public ReadOnlyReactiveProperty<Guid?> PendingCorrelationId => _pendingCorrelationId;
    public ReadOnlyReactiveProperty<byte[]?> TargetPublicKeyHash => _targetPublicKeyHash;
    public ReadOnlyReactiveProperty<ConnectionMode?> SelectedRouteMode => _selectedRouteMode;
    public ReadOnlyReactiveProperty<string?> DirectEndpoint => _directEndpoint;
    public ReadOnlyReactiveProperty<Guid?> RelayHostPeerId => _relayHostPeerId;
    public ReadOnlyReactiveProperty<string?> Phase => _phase;
    public ReadOnlyReactiveProperty<DateTimeOffset?> NotUntilUtc => _notUntilUtc;
    public ReadOnlyReactiveProperty<string?> LastError => _lastError;

    public IReadOnlyModelList<SimulatorHandshakeAttemptState> HandshakeAttempts => _handshakeAttempts;

    internal void Track(IDisposable disposable)
        => disposable.AddTo(ref _bag);

    public void SetDisplayName(string? displayName)
        => _displayName.Value = NormalizeDisplayName(displayName);

    public void SetOnline(bool isOnline)
    {
        _isOnline.Value = isOnline;

        if (!isOnline)
        {
            _uiState.Value = SimulatorPeerUiState.Offline;
        }
        else
        {
            if (_uiState.Value == SimulatorPeerUiState.Offline)
            {
                _uiState.Value = SimulatorPeerUiState.Ready;
                _pendingCorrelationId.Value = null;
            }
        }
    }

    public void SetRelayCapable(bool isRelayCapable)
        => _isRelayCapable.Value = isRelayCapable;

    public void SetTargetPublicKeyHash(byte[]? targetPublicKeyHash)
        => _targetPublicKeyHash.Value = targetPublicKeyHash;

    public void SetSelectedRouteMode(ConnectionMode? selectedRouteMode)
        => _selectedRouteMode.Value = selectedRouteMode;

    public void SetDirectEndpoint(string? directEndpoint)
        => _directEndpoint.Value = directEndpoint;

    public void SetRelayHostPeerId(Guid? relayHostPeerId)
        => _relayHostPeerId.Value = relayHostPeerId;

    public void SetPhase(string? phase)
        => _phase.Value = phase;

    public void SetNotUntilUtc(DateTimeOffset? notUntilUtc)
        => _notUntilUtc.Value = notUntilUtc;

    public void SetLastError(string? lastError)
        => _lastError.Value = lastError;

    public void MarkOutboundPending(Guid requestCorrelationId)
    {
        UpsertAttempt(requestCorrelationId);
        _uiState.Value = SimulatorPeerUiState.OutboundPending;
        _pendingCorrelationId.Value = requestCorrelationId;
    }

    public void MarkInboundPending(Guid requestCorrelationId)
    {
        UpsertAttempt(requestCorrelationId);
        _uiState.Value = SimulatorPeerUiState.InboundPending;
        _pendingCorrelationId.Value = requestCorrelationId;
    }

    public void MarkEstablished()
    {
        _uiState.Value = SimulatorPeerUiState.Established;
        _pendingCorrelationId.Value = null;
        _phase.Value = null;
        _notUntilUtc.Value = null;
        _lastError.Value = null;
    }

    public void MarkExpired()
    {
        _uiState.Value = SimulatorPeerUiState.Expired;
        _pendingCorrelationId.Value = null;
        _phase.Value = null;
    }

    public void ClearRuntimeState()
    {
        _uiState.Value = _isOnline.Value ? SimulatorPeerUiState.Ready : SimulatorPeerUiState.Offline;
        _pendingCorrelationId.Value = null;
        _targetPublicKeyHash.Value = null;
        _selectedRouteMode.Value = null;
        _directEndpoint.Value = null;
        _relayHostPeerId.Value = null;
        _phase.Value = null;
        _notUntilUtc.Value = null;
        _lastError.Value = null;
        _handshakeAttempts.Reset(new List<SimulatorHandshakeAttemptState>());
    }

    private void UpsertAttempt(Guid correlationId)
    {
        var attempts = _handshakeAttempts.GetSnapshot().ToList();

        var idx = attempts.FindIndex(a => a.CorrelationId == correlationId);
        var createdAt = idx >= 0 ? attempts[idx].CreatedAtUtc : DateTimeOffset.UtcNow;

        var snapshot = new SimulatorHandshakeAttemptState
        {
            CorrelationId = correlationId,
            TargetPublicKeyHash = _targetPublicKeyHash.Value,
            SelectedRouteMode = _selectedRouteMode.Value,
            DirectEndpoint = _directEndpoint.Value,
            RelayHostPeerId = _relayHostPeerId.Value,
            Phase = _phase.Value,
            NotUntilUtc = _notUntilUtc.Value,
            LastError = _lastError.Value,
            CreatedAtUtc = createdAt
        };

        if (idx >= 0) attempts[idx] = snapshot;
        else attempts.Add(snapshot);

        _handshakeAttempts.Reset(attempts);
    }

    public void SetAttemptPhase(Guid correlationId, string? phase)
        => UpdateAttempt(correlationId, a => a with { Phase = phase });

    public void SetAttemptError(Guid correlationId, string? error)
        => UpdateAttempt(correlationId, a => a with { LastError = error });

    public void SetAttemptNotUntil(Guid correlationId, DateTimeOffset? notUntilUtc)
        => UpdateAttempt(correlationId, a => a with { NotUntilUtc = notUntilUtc });

    private void UpdateAttempt(
        Guid correlationId,
        Func<SimulatorHandshakeAttemptState, SimulatorHandshakeAttemptState> update)
    {
        var attempts = _handshakeAttempts.GetSnapshot().ToList();
        var idx = attempts.FindIndex(a => a.CorrelationId == correlationId);
        if (idx < 0)
        {
            // Create a new attempt snapshot from current summary fields, then apply the update.
            var initial = new SimulatorHandshakeAttemptState
            {
                CorrelationId = correlationId,
                TargetPublicKeyHash = _targetPublicKeyHash.Value,
                SelectedRouteMode = _selectedRouteMode.Value,
                DirectEndpoint = _directEndpoint.Value,
                RelayHostPeerId = _relayHostPeerId.Value,
                Phase = _phase.Value,
                NotUntilUtc = _notUntilUtc.Value,
                LastError = _lastError.Value,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            attempts.Add(update(initial));
            _handshakeAttempts.Reset(attempts);
            return;
        }

        attempts[idx] = update(attempts[idx]);
        _handshakeAttempts.Reset(attempts);
    }

    public void Dispose()
    {
        _bag.Dispose();
        _displayName.Dispose();
        _isOnline.Dispose();
        _isRelayCapable.Dispose();

        _uiState.Dispose();
        _pendingCorrelationId.Dispose();
        _targetPublicKeyHash.Dispose();
        _selectedRouteMode.Dispose();
        _directEndpoint.Dispose();
        _relayHostPeerId.Dispose();
        _phase.Dispose();
        _notUntilUtc.Dispose();
        _lastError.Dispose();

        _handshakeAttempts.Dispose();
    }

    private static string? NormalizeDisplayName(string? displayName)
        => string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
}
