using ObservableCollections;
using Desktop.Wpf.Features.Simulator.Models;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using R3;
using System;
using System.Linq;
using Desktop.Wpf.Features.Chat;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatedPeerModel : IDisposable
{
    private DisposableBag _bag;
    private readonly ReactiveProperty<string?> _displayName;

    private readonly ReactiveProperty<SimulatorPeerUiState> _uiState;
    private readonly ReactiveProperty<Guid?> _inboundReverseSignalPendingCorrelationId;
    private readonly ReactiveProperty<byte[]?> _targetPublicKeyHash;
    private readonly ReactiveProperty<ConnectionMode?> _selectedRouteMode;
    private readonly ReactiveProperty<string?> _directEndpoint;
    private readonly ReactiveProperty<Percolator.Network.PeerId?> _relayHostPeerId;

    private readonly ReactiveProperty<ConnectionMode> _connectionMode;
    private readonly ReactiveProperty<string?> _host;
    private readonly ReactiveProperty<int> _port;
    private readonly ReactiveProperty<Percolator.Network.PeerId> _relayPeerId;
    private readonly ReactiveProperty<string?> _phase;
    private readonly ReactiveProperty<DateTimeOffset?> _notUntilUtc;
    private readonly ReactiveProperty<string?> _lastError;

    private readonly ReactiveProperty<IdentityPublicKeyHash?> _pendingStandardHandshakeToMainResponderPublicKeyHash;
    private readonly ReactiveProperty<Guid?> _pendingStandardHandshakeToMainTemporarySessionId;

    private readonly ObservableList<SimulatorHandshakeAttemptState> _handshakeAttempts;
    private readonly ReactiveProperty<int> _handshakeAttemptsVersion;

    public ObservableList<Guid> KnownPeerIds { get; }
    public ObservableList<SimulatedPublishedPreKeyBundleModel> PublishedPreKeyBundles { get; }

    private readonly ObservableDictionary<SessionId, SecureSession> _sessions;
    private readonly ObservableList<SimulatedSignedPreKeyModel> _signedPreKeys;
    private readonly ObservableList<SimulatedOutboundInviteModel> _outboundInvites;
    private readonly ObservableList<SimulatedPendingInviteHandshakeResponseModel> _pendingInviteHandshakeResponses;
    private readonly ObservableList<SimulatedChatMessageSnapshot> _recentChatMessages;
    private readonly ObservableList<SimulatedOneTimePreKeyPrivateRecord> _oneTimePreKeysPrivate;

    private readonly ObservableDictionary<string, SimulatedPendingStandardSignalHelloModel> _pendingInboundStandardSignalHellos;

    public SimulatedPeerModel(
        Percolator.Network.PeerId peerId,
        int selfIdentityId,
        string? displayName,
        bool isRelayCapable,
        byte[] identitySigningKeySpki,
        byte[] identitySigningKeyPrivateKeyEcPrivateKey,
        ConnectionMode connectionMode = Desktop.Wpf.Features.Simulator.ConnectionMode.Direct,
        string? host = null,
        int port = 0,
        Percolator.Network.PeerId? relayPeerId = null,
        SimulatorPeerUiState uiState = SimulatorPeerUiState.Ready,
        Guid? pendingCorrelationId = null,
        byte[]? targetPublicKeyHash = null,
        ConnectionMode? selectedRouteMode = null,
        string? directEndpoint = null,
        Percolator.Network.PeerId? relayHostPeerId = null,
        string? phase = null,
        DateTimeOffset? notUntilUtc = null,
        string? lastError = null,
        List<SimulatorHandshakeAttemptState>? handshakeAttempts = null,
        IdentityPublicKeyHash? pendingStandardHandshakeToMainResponderPublicKeyHash = null,
        Guid? pendingStandardHandshakeToMainTemporarySessionId = null,
        List<Guid>? knownPeerIds = null,
        List<SimulatedPublishedPreKeyBundleModel>? publishedPreKeyBundles = null)
    {
        PeerId = peerId;
        SelfIdentityId = selfIdentityId;

        IdentitySigningKeySpki = identitySigningKeySpki;
        IdentitySigningKeyPrivateKeyEcPrivateKey = identitySigningKeyPrivateKeyEcPrivateKey;

        _displayName = new ReactiveProperty<string?>(NormalizeDisplayName(displayName));
        IsRelayCapable = new ReactiveProperty<bool>(isRelayCapable);

        var ui = uiState;
        var pending = pendingCorrelationId;
        if (ui == SimulatorPeerUiState.Offline)
        {
            ui = SimulatorPeerUiState.Ready;
            pending = null;
        }

        _uiState = new ReactiveProperty<SimulatorPeerUiState>(ui);
        _inboundReverseSignalPendingCorrelationId = new ReactiveProperty<Guid?>(pending);
        _targetPublicKeyHash = new ReactiveProperty<byte[]?>(targetPublicKeyHash);
        _selectedRouteMode = new ReactiveProperty<ConnectionMode?>(selectedRouteMode);
        _directEndpoint = new ReactiveProperty<string?>(directEndpoint);
        _relayHostPeerId = new ReactiveProperty<Percolator.Network.PeerId?>(relayHostPeerId);

        _connectionMode = new ReactiveProperty<ConnectionMode>(connectionMode);
        _host = new ReactiveProperty<string?>(host);
        _port = new ReactiveProperty<int>(port);
        _relayPeerId = new ReactiveProperty<Percolator.Network.PeerId>(relayPeerId ?? new Percolator.Network.PeerId(Guid.Empty));
        _phase = new ReactiveProperty<string?>(phase);
        _notUntilUtc = new ReactiveProperty<DateTimeOffset?>(notUntilUtc);
        _lastError = new ReactiveProperty<string?>(lastError);

        _pendingStandardHandshakeToMainResponderPublicKeyHash = new ReactiveProperty<IdentityPublicKeyHash?>(pendingStandardHandshakeToMainResponderPublicKeyHash);
        _pendingStandardHandshakeToMainTemporarySessionId = new ReactiveProperty<Guid?>(pendingStandardHandshakeToMainTemporarySessionId);

        _handshakeAttempts = new ObservableList<SimulatorHandshakeAttemptState>();
        _handshakeAttempts.AddRange(handshakeAttempts ?? new());
        _handshakeAttemptsVersion = new ReactiveProperty<int>(0);

        KnownPeerIds = new ObservableList<Guid>();
        KnownPeerIds.AddRange(knownPeerIds ?? new());

        PublishedPreKeyBundles = new ObservableList<SimulatedPublishedPreKeyBundleModel>();
        PublishedPreKeyBundles.AddRange(publishedPreKeyBundles ?? new());

        _sessions = new ObservableDictionary<SessionId, SecureSession>();
        _signedPreKeys = new ObservableList<SimulatedSignedPreKeyModel>();
        _outboundInvites = new ObservableList<SimulatedOutboundInviteModel>();
        _pendingInviteHandshakeResponses = new ObservableList<SimulatedPendingInviteHandshakeResponseModel>();
        _recentChatMessages = new ObservableList<SimulatedChatMessageSnapshot>();
        _oneTimePreKeysPrivate = new ObservableList<SimulatedOneTimePreKeyPrivateRecord>();

        _pendingInboundStandardSignalHellos = new ObservableDictionary<string, SimulatedPendingStandardSignalHelloModel>(StringComparer.Ordinal);
    }

    public Percolator.Network.PeerId PeerId { get; }

    public int SelfIdentityId { get; }

    public byte[] IdentitySigningKeySpki { get; }
    internal byte[] IdentitySigningKeyPrivateKeyEcPrivateKey { get; }

    public ReadOnlyReactiveProperty<string?> DisplayName => _displayName;
    public ReactiveProperty<bool> IsRelayCapable { get; }

    public ReadOnlyReactiveProperty<SimulatorPeerUiState> UiState => _uiState;
    public ReadOnlyReactiveProperty<Guid?> InboundReverseSignalPendingCorrelationId => _inboundReverseSignalPendingCorrelationId;
    public ReadOnlyReactiveProperty<byte[]?> TargetPublicKeyHash => _targetPublicKeyHash;
    public ReadOnlyReactiveProperty<ConnectionMode?> SelectedRouteMode => _selectedRouteMode;
    public ReadOnlyReactiveProperty<string?> DirectEndpoint => _directEndpoint;
    public ReadOnlyReactiveProperty<Percolator.Network.PeerId?> RelayHostPeerId => _relayHostPeerId;

    public ReadOnlyReactiveProperty<ConnectionMode> ConnectionMode => _connectionMode;
    public ReadOnlyReactiveProperty<string?> Host => _host;
    public ReadOnlyReactiveProperty<int> Port => _port;
    public ReadOnlyReactiveProperty<Percolator.Network.PeerId> RelayPeerId => _relayPeerId;
    public ReadOnlyReactiveProperty<string?> Phase => _phase;
    public ReadOnlyReactiveProperty<DateTimeOffset?> NotUntilUtc => _notUntilUtc;
    public ReadOnlyReactiveProperty<string?> LastError => _lastError;

    public ReadOnlyReactiveProperty<IdentityPublicKeyHash?> PendingStandardHandshakeToMainResponderPublicKeyHash => _pendingStandardHandshakeToMainResponderPublicKeyHash;
    public ReadOnlyReactiveProperty<Guid?> PendingStandardHandshakeToMainTemporarySessionId => _pendingStandardHandshakeToMainTemporarySessionId;

    public IReadOnlyObservableList<SimulatorHandshakeAttemptState> HandshakeAttempts => _handshakeAttempts;
    public ReadOnlyReactiveProperty<int> HandshakeAttemptsVersion => _handshakeAttemptsVersion;

    public IReadOnlyObservableDictionary<SessionId, SecureSession> Sessions => _sessions;
    public IReadOnlyObservableList<SimulatedSignedPreKeyModel> SignedPreKeys => _signedPreKeys;
    public IReadOnlyObservableList<SimulatedOutboundInviteModel> OutboundInvites => _outboundInvites;
    public IReadOnlyObservableList<SimulatedPendingInviteHandshakeResponseModel> PendingInviteHandshakeResponses => _pendingInviteHandshakeResponses;
    public IReadOnlyObservableList<SimulatedChatMessageSnapshot> RecentChatMessages => _recentChatMessages;
    public IReadOnlyObservableList<SimulatedOneTimePreKeyPrivateRecord> OneTimePreKeysPrivate => _oneTimePreKeysPrivate;

    public IReadOnlyObservableDictionary<string, SimulatedPendingStandardSignalHelloModel> PendingInboundStandardSignalHellos => _pendingInboundStandardSignalHellos;

    internal ObservableDictionary<SessionId, SecureSession> SessionsMutable => _sessions;
    internal ObservableList<SimulatedSignedPreKeyModel> SignedPreKeysMutable => _signedPreKeys;
    internal ObservableList<SimulatedOutboundInviteModel> OutboundInvitesMutable => _outboundInvites;
    internal ObservableList<SimulatedPendingInviteHandshakeResponseModel> PendingInviteHandshakeResponsesMutable => _pendingInviteHandshakeResponses;
    internal ObservableList<SimulatedChatMessageSnapshot> RecentChatMessagesMutable => _recentChatMessages;
    internal ObservableList<SimulatedOneTimePreKeyPrivateRecord> OneTimePreKeysPrivateMutable => _oneTimePreKeysPrivate;

    internal ObservableDictionary<string, SimulatedPendingStandardSignalHelloModel> PendingInboundStandardSignalHellosMutable => _pendingInboundStandardSignalHellos;

    internal void Track(IDisposable disposable)
        => disposable.AddTo(ref _bag);

    public void SetDisplayName(string? displayName)
        => _displayName.Value = NormalizeDisplayName(displayName);

    public void AddChatMessage(bool isFromMain, string content, DateTimeOffset receivedUtc)
    {
        var m = new SimulatedChatMessageSnapshot(isFromMain, content, receivedUtc);
        _recentChatMessages.Add(m);
        if (_recentChatMessages.Count > 50)
        {
            _recentChatMessages.RemoveAt(0);
        }
    }

    internal bool TryPopOneTimePreKeyPrivate(SimulatedOneTimePreKeyId id, out PrivatePreKey privateKey)
    {
        for (var i = 0; i < _oneTimePreKeysPrivate.Count; i++)
        {
            if (_oneTimePreKeysPrivate[i].Id == id)
            {
                privateKey = _oneTimePreKeysPrivate[i].PrivateKey;
                _oneTimePreKeysPrivate.RemoveAt(i);
                return true;
            }
        }
        privateKey = default;
        return false;
    }

    
    public void SetSelectedRouteMode(ConnectionMode? selectedRouteMode)
        => _selectedRouteMode.Value = selectedRouteMode;

    public void SetDirectEndpoint(string? directEndpoint)
        => _directEndpoint.Value = directEndpoint;

    public void SetRelayHostPeerId(Percolator.Network.PeerId? relayHostPeerId)
        => _relayHostPeerId.Value = relayHostPeerId;

    public void SetConnection(ConnectionMode mode, string? host, int port, Percolator.Network.PeerId relayPeerId)
    {
        _connectionMode.Value = mode;
        _host.Value = host;
        _port.Value = port;
        _relayPeerId.Value = relayPeerId;
    }

    public void SetPhase(string? phase)
        => _phase.Value = phase;

    public void SetNotUntilUtc(DateTimeOffset? notUntilUtc)
        => _notUntilUtc.Value = notUntilUtc;

    public void SetLastError(string? lastError)
        => _lastError.Value = lastError;

    public void SetPendingStandardHandshakeToMain(IdentityPublicKeyHash? responderPublicKeyHash, Guid? temporarySessionId)
    {
        _pendingStandardHandshakeToMainResponderPublicKeyHash.Value = responderPublicKeyHash;
        _pendingStandardHandshakeToMainTemporarySessionId.Value = temporarySessionId;
    }

    public void ClearPendingStandardHandshakeToMain()
        => SetPendingStandardHandshakeToMain(null, null);

    public void MarkOutboundPending(Guid requestCorrelationId)
    {
        UpsertAttempt(requestCorrelationId);
        _uiState.Value = SimulatorPeerUiState.OutboundPending;
        _inboundReverseSignalPendingCorrelationId.Value = requestCorrelationId;
    }

    public void MarkInboundPending(Guid requestCorrelationId)
    {
        UpsertAttempt(requestCorrelationId);
        _uiState.Value = SimulatorPeerUiState.AwaitingUserAcceptance;
        _inboundReverseSignalPendingCorrelationId.Value = requestCorrelationId;
    }

    internal void MarkAwaitingUserAcceptance()
        => _uiState.Value = SimulatorPeerUiState.AwaitingUserAcceptance;

    internal void ClearInboundReverseSignalPendingCorrelationId()
        => _inboundReverseSignalPendingCorrelationId.Value = null;

    public void MarkEstablished()
    {
        _uiState.Value = SimulatorPeerUiState.Established;
        _inboundReverseSignalPendingCorrelationId.Value = null;
        _phase.Value = null;
        _notUntilUtc.Value = null;
        _lastError.Value = null;

        ClearPendingStandardHandshakeToMain();
        _pendingInboundStandardSignalHellos.Clear();
    }

    public void MarkExpired()
    {
        _uiState.Value = SimulatorPeerUiState.Expired;
        _inboundReverseSignalPendingCorrelationId.Value = null;
        _phase.Value = null;
    }

    public void ClearRuntimeState()
    {
        _uiState.Value = SimulatorPeerUiState.Ready;
        _inboundReverseSignalPendingCorrelationId.Value = null;
        _targetPublicKeyHash.Value = null;
        _selectedRouteMode.Value = null;
        _directEndpoint.Value = null;
        _relayHostPeerId.Value = null;
        _phase.Value = null;
        _notUntilUtc.Value = null;
        _lastError.Value = null;

        _pendingInboundStandardSignalHellos.Clear();

        _handshakeAttempts.Clear();
        _handshakeAttemptsVersion.Value++;
    }

    internal void UpsertAttempt(Guid requestCorrelationId)
    {
        var idx = -1;
        for (var i = 0; i < _handshakeAttempts.Count; i++)
        {
            if (_handshakeAttempts[i].CorrelationId == requestCorrelationId)
            {
                idx = i;
                break;
            }
        }

        if (idx < 0)
        {
            _handshakeAttempts.Add(new SimulatorHandshakeAttemptState
            {
                CorrelationId = requestCorrelationId,
                TargetPublicKeyHash = _targetPublicKeyHash.Value,
                SelectedRouteMode = _selectedRouteMode.Value,
                DirectEndpoint = _directEndpoint.Value,
                RelayHostPeerId = _relayHostPeerId.Value?.Value,
                Phase = _phase.Value,
                NotUntilUtc = _notUntilUtc.Value,
                LastError = _lastError.Value,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });

            _handshakeAttemptsVersion.Value++;
            return;
        }

        var existing = _handshakeAttempts[idx];
        _handshakeAttempts[idx] = existing with
        {
            TargetPublicKeyHash = _targetPublicKeyHash.Value,
            SelectedRouteMode = _selectedRouteMode.Value,
            DirectEndpoint = _directEndpoint.Value,
            RelayHostPeerId = _relayHostPeerId.Value.Value != Guid.Empty ? _relayHostPeerId.Value.Value : null,
            Phase = _phase.Value,
            NotUntilUtc = _notUntilUtc.Value,
            LastError = _lastError.Value,
        };

        _handshakeAttemptsVersion.Value++;
    }

    public void SetAttemptPhase(Guid correlationId, string? phase)
        => UpdateAttempt(correlationId, a => a with { Phase = phase });

    public void SetAttemptError(Guid correlationId, string? error)
        => UpdateAttempt(correlationId, a => a with { LastError = error });

    public void SetAttemptNotUntil(Guid correlationId, DateTimeOffset? notUntilUtc)
        => UpdateAttempt(correlationId, a => a with { NotUntilUtc = notUntilUtc });

    internal PeerStateSnapshot Freeze()
    {
        return new PeerStateSnapshot(
            PeerId: PeerId,
            SelfIdentityId: SelfIdentityId,
            DisplayName: _displayName.Value,
            IsRelayCapable: IsRelayCapable.Value,
            IdentitySigningKeySpki: IdentitySigningKeySpki.ToArray(),
            IdentitySigningKeyPrivateKeyEcPrivateKey: IdentitySigningKeyPrivateKeyEcPrivateKey.ToArray(),
            ConnectionMode: _connectionMode.Value,
            Host: _host.Value,
            Port: _port.Value,
            RelayPeerId: _relayPeerId.Value,
            UiState: _uiState.Value,
            InboundReverseSignalPendingCorrelationId: _inboundReverseSignalPendingCorrelationId.Value,
            TargetPublicKeyHash: _targetPublicKeyHash.Value?.ToArray(),
            SelectedRouteMode: _selectedRouteMode.Value,
            DirectEndpoint: _directEndpoint.Value,
            RelayHostPeerId: _relayHostPeerId.Value,
            Phase: _phase.Value,
            NotUntilUtc: _notUntilUtc.Value,
            LastError: _lastError.Value,
            HandshakeAttempts: _handshakeAttempts.ToList(),
            PendingStandardHandshakeToMainResponderPublicKeyHash: _pendingStandardHandshakeToMainResponderPublicKeyHash.Value,
            PendingStandardHandshakeToMainTemporarySessionId: _pendingStandardHandshakeToMainTemporarySessionId.Value,
            KnownPeerIds: KnownPeerIds.ToList(),
            PublishedPreKeyBundles: PublishedPreKeyBundles
                .Select(b => new PublishedPreKeyBundleSnapshot(
                    b.RecipientPublicKeyHash,
                    b.LogicalOwnerPeerId,
                    b.IdentityKey,
                    b.SignedPreKeyId,
                    b.SignedPreKey,
                    b.PreKeySignature,
                    b.OneTimeKeys.Select(otk => new OneTimeKeySnapshot(otk.Id, otk.Key.ToArray())).ToList(),
                    b.ExpiresUtc))
                .ToList(),
            Sessions: _sessions
                .Select(kvp => kvp.Value)
                .Select(s => new SessionSnapshot(
                    SessionId: s.Id.Value,
                    RemotePeerId: s.RemotePeerId.Value,
                    ProtocolVersion: s.ProtocolVersion.Value,
                    RootKey: s.State.RootKey.ToArray(),
                    SendChainKey: s.State.SendingChainKey?.ToArray(),
                    SendCounter: s.State.SendingCounter,
                    RecvChainKey: s.State.ReceivingChainKey?.ToArray(),
                    RecvCounter: s.State.ReceivingCounter,
                    PrevChainLength: s.State.PreviousChainLength,
                    RemoteRatchetKey: s.State.RemoteRatchetKey?.ToArray(),
                    DhRatchetPrivateKey: s.State.DhRatchetPrivateKey?.ToArray(),
                    SkippedKeysCount: s.SkippedKeysCount,
                    CreatedAtUtc: s.CreatedAtUtc,
                    LastUsedAtUtc: s.LastUsedAtUtc))
                .ToList(),
            SignedPreKeys: _signedPreKeys
                .Select(s => new SignedPreKeySnapshot(s.SignedPreKeyId, s.PrivateEcPrivateKey.ToArray(), s.PublicSpki.ToArray()))
                .ToList(),
            OutboundInvites: _outboundInvites
                .Select(i => new OutboundInviteSnapshot(i.CorrelationId, i.SignedPreKeyPrivateEcPrivateKey.ToArray()))
                .ToList(),
            PendingInviteHandshakeResponses: _pendingInviteHandshakeResponses
                .Select(r => new PendingInviteHandshakeResponseSnapshot(r.CorrelationId, r.ResponseBytes.ToArray()))
                .ToList(),
            RecentChatMessages: _recentChatMessages.ToList(),
            OneTimePreKeysPrivate: _oneTimePreKeysPrivate
                .Select(r => new SimulatedOneTimePreKeyPrivateSnapshot(
                    r.Id.Value,
                    r.PrivateKey.ToArray(),
                    r.CreatedAtUtc))
                .ToList());
    }

    private void UpdateAttempt(
        Guid correlationId,
        Func<SimulatorHandshakeAttemptState, SimulatorHandshakeAttemptState> update)
    {
        var idx = -1;
        for (var i = 0; i < _handshakeAttempts.Count; i++)
        {
            if (_handshakeAttempts[i].CorrelationId == correlationId)
            {
                idx = i;
                break;
            }
        }
        if (idx < 0)
        {
            // Create a new attempt snapshot from current summary fields, then apply the update.
            var initial = new SimulatorHandshakeAttemptState
            {
                CorrelationId = correlationId,
                TargetPublicKeyHash = _targetPublicKeyHash.Value,
                SelectedRouteMode = _selectedRouteMode.Value,
                DirectEndpoint = _directEndpoint.Value,
                RelayHostPeerId = _relayHostPeerId.Value.Value != Guid.Empty ? _relayHostPeerId.Value.Value : null,
                Phase = _phase.Value,
                NotUntilUtc = _notUntilUtc.Value,
                LastError = _lastError.Value,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            _handshakeAttempts.Add(update(initial));
            _handshakeAttemptsVersion.Value++;
            return;
        }

        _handshakeAttempts[idx] = update(_handshakeAttempts[idx]);
        _handshakeAttemptsVersion.Value++;
    }

    public void Dispose()
    {
        _bag.Dispose();
        _displayName.Dispose();
        IsRelayCapable.Dispose();

        _uiState.Dispose();
        _inboundReverseSignalPendingCorrelationId.Dispose();
        _targetPublicKeyHash.Dispose();
        _selectedRouteMode.Dispose();
        _directEndpoint.Dispose();
        _relayHostPeerId.Dispose();

        _connectionMode.Dispose();
        _host.Dispose();
        _port.Dispose();
        _relayPeerId.Dispose();
        _phase.Dispose();
        _notUntilUtc.Dispose();
        _lastError.Dispose();

        _handshakeAttemptsVersion.Dispose();
    }

    private static string? NormalizeDisplayName(string? displayName)
        => string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
}

public sealed record SimulatedSignedPreKeyModel(Guid SignedPreKeyId, byte[] PrivateEcPrivateKey, byte[] PublicSpki);

public sealed record SimulatedOutboundInviteModel(Guid CorrelationId, byte[] SignedPreKeyPrivateEcPrivateKey);

public sealed record SimulatedPendingInviteHandshakeResponseModel(Guid CorrelationId, byte[] ResponseBytes);

public sealed record SimulatedPublishedPreKeyBundleModel(
    IdentityPublicKeyHash RecipientPublicKeyHash,
    Percolator.Network.PeerId LogicalOwnerPeerId,
    byte[] IdentityKey,
    Guid SignedPreKeyId,
    byte[] SignedPreKey,
    byte[] PreKeySignature,
    ObservableList<Percolator.Cryptography.OneTimeKeyInstance> OneTimeKeys,
    DateTimeOffset ExpiresUtc);
