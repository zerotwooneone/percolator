using System.Security.Cryptography;
using Desktop.Wpf.Features.Simulator.Models;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;
using R3;
using Percolator.Contracts;
using Percolator.Application.Configuration;
using Percolator.Network;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatedPeerItemViewModel : IDisposable
{
    private readonly ISimulatorInitializer _directory;
    private readonly SimulatedPeerModel _model;
    private readonly ISimulatorStateService _state;
    private readonly ISimulatorDiagnosticsService _diagnostics;
    private readonly Percolator.Application.Network.IMainReverseSignalInviteFactory _inviteFactory;
    private readonly Percolator.Application.Network.IAdvertisedHostLookup _advertisedHostLookup;
    private readonly Percolator.Infrastructure.Network.Grpc.PercolatorMessageService _messageService;
    private readonly ISimulatorToMainTransportService _toMain;
    private readonly IOptions<TransportOptions> _transportOptions;
    private readonly Percolator.Application.Identity.ActiveIdentityContext _active;
    private readonly Func<Percolator.Network.NetworkPeerId?> _getSelectedRelayPeerId;
    private DisposableBag _bag;

    private Percolator.Cryptography.SessionId? _sessionToMain;

    public SimulatedPeerItemViewModel(
        ISimulatorInitializer directory,
        SimulatedPeerModel model,
        ISimulatorStateService state,
        ISimulatorToMainTransportService toMain,
        ISimulatorDiagnosticsService diagnostics,
        Percolator.Application.Network.IMainReverseSignalInviteFactory inviteFactory,
        Percolator.Application.Network.IAdvertisedHostLookup advertisedHostLookup,
        Percolator.Infrastructure.Network.Grpc.PercolatorMessageService messageService,
        IOptions<TransportOptions> transportOptions,
        Percolator.Application.Identity.ActiveIdentityContext active,
        Func<Percolator.Network.NetworkPeerId?> getSelectedRelayPeerId)
    {
        _directory = directory;
        _model = model;
        _state = state;
        _toMain = toMain;
        _diagnostics = diagnostics;
        _inviteFactory = inviteFactory;
        _advertisedHostLookup = advertisedHostLookup;
        _messageService = messageService;
        _transportOptions = transportOptions;
        _active = active;
        _getSelectedRelayPeerId = getSelectedRelayPeerId;

        DisplayText = _model.DisplayName
            .ObserveOnCurrentSynchronizationContext()
            .Select(name => string.IsNullOrWhiteSpace(name) ? _model.NetworkPeerId.ToString()[..8] : name!)
            .ToBindableReactiveProperty(_model.NetworkPeerId.ToString()[..8])
            .AddTo(ref _bag);

        RuntimeStateText = _model.UiState
            .ObserveOnCurrentSynchronizationContext()
            .Select(s => s.ToString())
            .ToBindableReactiveProperty(_model.UiState.CurrentValue.ToString())
            .AddTo(ref _bag);

        ShowMarkOutboundPending = _model.UiState
            .ObserveOnCurrentSynchronizationContext()
            .Select(s => s != SimulatorPeerUiState.OutboundPending)
            .ToBindableReactiveProperty(true)
            .AddTo(ref _bag);

        ShowMarkInboundPending = _model.UiState
            .ObserveOnCurrentSynchronizationContext()
            .Select(s => s != SimulatorPeerUiState.AwaitingUserAcceptance)
            .ToBindableReactiveProperty(true)
            .AddTo(ref _bag);

        ShowMarkEstablished = _model.UiState
            .ObserveOnCurrentSynchronizationContext()
            .Select(s => s != SimulatorPeerUiState.Established)
            .ToBindableReactiveProperty(true)
            .AddTo(ref _bag);

        ShowAcceptRejectInboundPending = _model.UiState
            .ObserveOnCurrentSynchronizationContext()
            .Select(s => s == SimulatorPeerUiState.AwaitingUserAcceptance)
            .ToBindableReactiveProperty(false)
            .AddTo(ref _bag);

        ShowClearRuntimeState = _model.UiState
            .ObserveOnCurrentSynchronizationContext()
            .Select(s => s != SimulatorPeerUiState.Ready)
            .ToBindableReactiveProperty(true)
            .AddTo(ref _bag);

        var toggleRelayCapable = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        toggleRelayCapable.AsObservable()
            .Subscribe(_ => _model.IsRelayCapable.Value = !_model.IsRelayCapable.CurrentValue)
            .AddTo(ref _bag);
        ToggleRelayCapableCommand = toggleRelayCapable.AddTo(ref _bag);

        var remove = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        remove.AsObservable()
            .SubscribeAwait(async (_, ct) => await _state.RemovePeerAsync(_model.NetworkPeerId, ct), AwaitOperation.Drop)
            .AddTo(ref _bag);
        RemoveCommand = remove.AddTo(ref _bag);

        MarkOutboundPendingCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => _model.MarkOutboundPending(Guid.NewGuid()))
            .AddTo(ref _bag);

        MarkInboundPendingCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => _model.MarkInboundPending(Guid.NewGuid()))
            .AddTo(ref _bag);

        MarkEstablishedCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => _model.MarkEstablished())
            .AddTo(ref _bag);

        ClearRuntimeStateCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => _model.ClearRuntimeState())
            .AddTo(ref _bag);

        MainInviteDirectCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => { })
            .AddTo(ref _bag);
        MainInviteDirectCommand.AsObservable()
            .SubscribeAwait(async (_, ct) => await ExecuteMainInviteDirectAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);

        MainInviteRelayedCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => { })
            .AddTo(ref _bag);
        MainInviteRelayedCommand.AsObservable()
            .SubscribeAwait(async (_, ct) => await ExecuteMainInviteRelayedAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);

        RelayForwardToMainCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => { })
            .AddTo(ref _bag);
        RelayForwardToMainCommand.AsObservable()
            .SubscribeAwait(async (_, ct) => await ExecuteRelayForwardToMainAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);

        PeerInviteDirectCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => { })
            .AddTo(ref _bag);
        PeerInviteDirectCommand.AsObservable()
            .SubscribeAwait(async (_, ct) => await ExecutePeerInviteDirectAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);

        PeerInviteRelayedCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => { })
            .AddTo(ref _bag);
        PeerInviteRelayedCommand.AsObservable()
            .SubscribeAwait(async (_, ct) => await ExecutePeerInviteRelayedAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);

        PublishStandardPreKeysToRelayCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => { })
            .AddTo(ref _bag);
        PublishStandardPreKeysToRelayCommand.AsObservable()
            .SubscribeAwait(async (_, ct) => await ExecutePublishStandardPreKeysToRelayAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);

        PeerStandardHandshakeToMainRelayedCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => { })
            .AddTo(ref _bag);
        PeerStandardHandshakeToMainRelayedCommand.AsObservable()
            .SubscribeAwait(async (_, ct) => await ExecutePeerStandardHandshakeToMainRelayedAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);
    }

    public NetworkPeerId NetworkPeerId => _model.NetworkPeerId;

    public BindableReactiveProperty<string> DisplayText { get; }

    public BindableReactiveProperty<string> RuntimeStateText { get; }

    public BindableReactiveProperty<bool> ShowMarkOutboundPending { get; }
    public BindableReactiveProperty<bool> ShowMarkInboundPending { get; }
    public BindableReactiveProperty<bool> ShowMarkEstablished { get; }
    public BindableReactiveProperty<bool> ShowClearRuntimeState { get; }
    public BindableReactiveProperty<bool> ShowAcceptRejectInboundPending { get; }

    public ReactiveCommand<Unit> ToggleRelayCapableCommand { get; }
    public ReactiveCommand<Unit> RemoveCommand { get; }

    public ReactiveCommand<Unit> MarkOutboundPendingCommand { get; }
    public ReactiveCommand<Unit> MarkInboundPendingCommand { get; }
    public ReactiveCommand<Unit> MarkEstablishedCommand { get; }
    public ReactiveCommand<Unit> ClearRuntimeStateCommand { get; }

    public ReactiveCommand<Unit> MainInviteDirectCommand { get; }
    public ReactiveCommand<Unit> MainInviteRelayedCommand { get; }
    public ReactiveCommand<Unit> RelayForwardToMainCommand { get; }

    public ReactiveCommand<Unit> PeerInviteDirectCommand { get; }
    public ReactiveCommand<Unit> PeerInviteRelayedCommand { get; }

    public ReactiveCommand<Unit> PublishStandardPreKeysToRelayCommand { get; }
    public ReactiveCommand<Unit> PeerStandardHandshakeToMainRelayedCommand { get; }

    private async Task ExecuteMainInviteDirectAsync(System.Threading.CancellationToken ct)
    {
        if (_active.Identity is null)
        {
            return;
        }

        var invite = _inviteFactory.CreateInvite();
        var inviterPeerId = _active.Identity is not null ? new NetworkPeerId(_active.Identity.SelfIdentityId.Value) : new NetworkPeerId(0);

        var acceptance = await _state.AcceptInboundDirectInviteAsync(
                simulatedNetworkPeerId: _model.NetworkPeerId,
                inviterNetworkPeerId: inviterPeerId,
                invite: invite,
                cancellationToken: ct)
            .ConfigureAwait(false);

        _sessionToMain = acceptance.SessionId;

        _ = await _toMain.DeliverInviteHandshakeResponseToMainAsync(acceptance.Response, ct).ConfigureAwait(false);
    }

    private async Task ExecuteMainInviteRelayedAsync(System.Threading.CancellationToken ct)
    {
        if (_active.Identity is null)
        {
            return;
        }

        var relayPeerId = _getSelectedRelayPeerId();
        if (relayPeerId is null)
        {
            await ExecuteMainInviteDirectAsync(ct).ConfigureAwait(false);
            return;
        }

        var invite = _inviteFactory.CreateInvite();

        var selfPkh = await _state.ComputePublicKeyHashAsync(_model.NetworkPeerId, ct).ConfigureAwait(false);

        await _state.EnqueueRelayDownstreamToPeerAsync(
                relayHostNetworkPeerId: relayPeerId.Value,
                targetIdentityPublicKeyHash: Percolator.Identity.IdentityPublicKeyHash.FromBytes(selfPkh),
                opaqueBytes: invite.ToByteArray(),
                debugType: nameof(EstablishDirectSessionRequest),
                cancellationToken: ct)
            .ConfigureAwait(false);

        var dequeued = await _state.DequeueRelayDownstreamToPeerAsync(
                relayHostNetworkPeerId: relayPeerId.Value,
                targetIdentityPublicKeyHash: Percolator.Identity.IdentityPublicKeyHash.FromBytes(selfPkh),
                max: 1,
                cancellationToken: ct)
            .ConfigureAwait(false);

        if (dequeued.Count == 0) return;

        var req = EstablishDirectSessionRequest.Parser.ParseFrom(dequeued[0].OpaqueBytes);
        var inviterPeerId = _active.Identity is not null ? new NetworkPeerId(_active.Identity.SelfIdentityId.Value) : new NetworkPeerId(0);

        var acceptance = await _state.AcceptInboundDirectInviteAsync(
                simulatedNetworkPeerId: _model.NetworkPeerId,
                inviterNetworkPeerId: inviterPeerId,
                invite: req,
                cancellationToken: ct)
            .ConfigureAwait(false);

        _sessionToMain = acceptance.SessionId;

        await _state.EnqueueRelayUpstreamToMainAsync(
                relayHostNetworkPeerId: relayPeerId.Value,
                opaqueBytes: acceptance.Response.ToByteArray(),
                debugType: nameof(InviteHandshakeResponse),
                cancellationToken: ct)
            .ConfigureAwait(false);
    }

    private bool HasActiveSessionToHost(NetworkPeerId relayHostNetworkPeerId)
    {
        return _state.Relationships.Contains(new PeerRelationship(relayHostNetworkPeerId, _model.NetworkPeerId, RelationshipType.RelayActiveSession));
    }

    private async Task ExecuteRelayForwardToMainAsync(System.Threading.CancellationToken ct)
    {
        var relayPeerId = _getSelectedRelayPeerId();
        if (relayPeerId is null)
        {
            return;
        }

        if (_active.Identity is null)
        {
            return;
        }

        if (_model.NetworkPeerId != relayPeerId)
        {
            return;
        }

        if (_sessionToMain is null)
        {
            return;
        }

        if (_active.Keys is null)
        {
            return;
        }

        var mainSpki = _active.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo();
        _ = SHA256.HashData(mainSpki);

        await _state.ForwardRelayUpstreamToMainAsync(
                relayHostNetworkPeerId: relayPeerId.Value,
                relayHostToMainSessionId: _sessionToMain,
                max: 250,
                cancellationToken: ct)
            .ConfigureAwait(false);
    }

    private async Task ExecutePeerInviteDirectAsync(System.Threading.CancellationToken ct)
    {
        var invite = CreatePeerToMainInvite();

        var ctx = new ServerCallContextStub(
            method: "/percolator.contracts.TransportService/EstablishDirectSession",
            peer: "ipv4:127.0.0.1:0",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: new Metadata(),
            cancellationToken: ct);

        await _messageService.EstablishDirectSession(invite, ctx).ConfigureAwait(false);
    }

    private Task ExecutePeerInviteRelayedAsync(System.Threading.CancellationToken ct)
    {
        if (_active.Identity is null)
        {
            return Task.CompletedTask;
        }

        var relayPeerId = _getSelectedRelayPeerId();
        if (relayPeerId is null)
        {
            return ExecutePeerInviteDirectAsync(ct);
        }

        var invite = CreatePeerToMainInvite();

        if (_active.Keys is null)
        {
            return Task.CompletedTask;
        }

        var mainSpki = _active.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo();
        _ = SHA256.HashData(mainSpki);

        return _state.EnqueueRelayUpstreamToMainAsync(
            relayHostNetworkPeerId: relayPeerId.Value,
            opaqueBytes: invite.ToByteArray(),
            debugType: nameof(EstablishDirectSessionRequest),
            cancellationToken: ct);
    }

    private async Task ExecutePublishStandardPreKeysToRelayAsync(CancellationToken ct)
    {
        var relayPeerId = _getSelectedRelayPeerId();
        if (relayPeerId is null)
        {
            return;
        }

        if (!HasActiveSessionToHost(relayPeerId.Value))
        {
            _diagnostics.Emit(
                SimulatorDiagnosticEventType.PreKeyPublishBlockedMissingActiveSession,
                $"Pre-key publish blocked (missing active session): publisher={_model.NetworkPeerId.Value.ToString()[..8]} relay={relayPeerId.Value.ToString()[..8]}",
                peerId: _model.NetworkPeerId,
                relayHostPeerId: relayPeerId);
            return;
        }

        await _state.PublishStandardPreKeyBundleToRelayAsync(
                simulatedNetworkPeerId: _model.NetworkPeerId,
                relayHostNetworkPeerId: relayPeerId.Value,
                expiresUtc: DateTimeOffset.UtcNow.AddHours(12),
                oneTimeKeyCount: 5,
                cancellationToken: ct)
            .ConfigureAwait(false);
    }

    private async Task ExecutePeerStandardHandshakeToMainRelayedAsync(CancellationToken ct)
    {
        if (_active.Keys is null)
        {
            return;
        }

        var notUntil = _model.NotUntilUtc.CurrentValue;
        if (notUntil.HasValue && notUntil.Value > DateTimeOffset.UtcNow)
        {
            return;
        }

        var relayPeerId = _getSelectedRelayPeerId();
        if (relayPeerId is null)
        {
            return;
        }

        var mainSpki = _active.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo();
        var mainPkh = Percolator.Identity.IdentityPublicKeyHash.FromSpki(mainSpki);

        _ = await _state.InitiateStandardHandshakeToMainByRelayPkhAsync(
                simulatedNetworkPeerId: _model.NetworkPeerId,
                relayHostNetworkPeerId: relayPeerId.Value,
                responderPublicKeyHash: mainPkh,
                cancellationToken: ct)
            .ConfigureAwait(false);
    }

    private EstablishDirectSessionRequest CreatePeerToMainInvite()
    {
        // Simulated peer inviter must advertise its simulator endpoint so the main app can route responses back in-process.
        var port = _transportOptions.Value.SimulatorPort;
        if (port == 0) port = 5002;

        var inviterHost = AllocateSimulatorLoopbackHost(_model.NetworkPeerId);

        using var identityEcdh = ECDiffieHellman.Create();
        identityEcdh.ImportECPrivateKey(_model.IdentitySigningKeyPrivateKeyEcPrivateKey, out _);
        var p256 = ECCurve.NamedCurves.nistP256.Oid.Value;
        var ikCurve = identityEcdh.ExportParameters(false).Curve.Oid.Value;
        if (!string.Equals(ikCurve, p256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Simulated peer identity key is not P-256 (CurveOid={ikCurve}). Restart to regenerate simulator keys.");
        }
        using var identityEcdsa = ECDsa.Create(identityEcdh.ExportParameters(true));

        var curve = identityEcdh.ExportParameters(false).Curve;
        using var inviterSignedPreKey = ECDiffieHellman.Create(curve);
        var inviterSignedPreKeySpki = inviterSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var inviterSignedPreKeyPriv = inviterSignedPreKey.ExportECPrivateKey();
        var preKeySig = identityEcdsa.SignData(inviterSignedPreKeySpki, HashAlgorithmName.SHA256);

        var correlation = Guid.NewGuid();
        _model.OutboundInvitesMutable.Add(new SimulatedOutboundInviteModel(correlation, inviterSignedPreKeyPriv));
        var payload = new InviteHandshakeRequestPayload
        {
            Version = 1,
            InviterHost = inviterHost,
            InviterPort = (uint)port,
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(10)),
            RequestCorrelationId = correlation.ToString(),
            InviterPreKey = new InviteHandshakePreKeyBundle
            {
                Version = 1,
                InviterSignedPreKey = ByteString.CopyFrom(inviterSignedPreKeySpki),
                PreKeySignature = ByteString.CopyFrom(preKeySig)
            }
        };

        var payloadBytes = payload.ToByteArray();
        var payloadSig = identityEcdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);

        return new EstablishDirectSessionRequest
        {
            Version = 1,
            InviterIdentityKey = ByteString.CopyFrom(_model.IdentitySigningKeySpki),
            Payload = ByteString.CopyFrom(payloadBytes),
            PayloadSignature = ByteString.CopyFrom(payloadSig)
        };
    }

    private static string AllocateSimulatorLoopbackHost(NetworkPeerId networkPeerId)
    {
        // Stable mapping of uint -> 127.77.X.Y. Keep within 1..254 to avoid network/broadcast edge cases.
        var value = networkPeerId.Value;
        var x = (byte)((value % 254) + 1);
        var y = (byte)(((value / 254) % 254) + 1);
        return $"127.77.{x}.{y}";
    }

    private sealed class ServerCallContextStub : ServerCallContext
    {
        private readonly string _method;
        private readonly string _peer;
        private readonly DateTime _deadline;
        private readonly Metadata _requestHeaders;
        private readonly CancellationToken _cancellationToken;

        public ServerCallContextStub(string method, string peer, DateTime deadline, Metadata requestHeaders, CancellationToken cancellationToken)
        {
            _method = method;
            _peer = peer;
            _deadline = deadline;
            _requestHeaders = requestHeaders;
            _cancellationToken = cancellationToken;
        }

        protected override string MethodCore => _method;
        protected override string HostCore => "localhost";
        protected override string PeerCore => _peer;
        protected override DateTime DeadlineCore => _deadline;
        protected override Metadata RequestHeadersCore => _requestHeaders;
        protected override CancellationToken CancellationTokenCore => _cancellationToken;
        protected override Metadata ResponseTrailersCore { get; } = new Metadata();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore { get; } = new AuthContext(null, new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotImplementedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }

    public void Dispose()
    {
        _bag.Dispose();
    }
}
