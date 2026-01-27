using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;
using R3;
using Percolator.Contracts;
using Percolator.Application.Configuration;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatedPeerItemViewModel : IDisposable
{
    private readonly ISimulatedPeerDirectory _directory;
    private readonly SimulatedPeerModel _model;
    private readonly IReverseSignalInviteFactory _inviteFactory;
    private readonly ISimulatedPeerRuntimeService _peerRuntime;
    private readonly ISimulatorRelayEmulator _relay;
    private readonly Percolator.Application.Network.PercolatorMessageService _messageService;
    private readonly IOptions<TransportOptions> _transportOptions;
    private readonly Percolator.Application.Identity.ActiveIdentityContext _active;
    private readonly Func<Guid?> _getSelectedRelayPeerId;
    private DisposableBag _bag;

    private Percolator.Cryptography.SessionId? _sessionToMain;

    public SimulatedPeerItemViewModel(
        ISimulatedPeerDirectory directory,
        SimulatedPeerModel model,
        IReverseSignalInviteFactory inviteFactory,
        ISimulatedPeerRuntimeService peerRuntime,
        ISimulatorRelayEmulator relay,
        Percolator.Application.Network.PercolatorMessageService messageService,
        IOptions<TransportOptions> transportOptions,
        Percolator.Application.Identity.ActiveIdentityContext active,
        Func<Guid?> getSelectedRelayPeerId)
    {
        _directory = directory;
        _model = model;
        _inviteFactory = inviteFactory;
        _peerRuntime = peerRuntime;
        _relay = relay;
        _messageService = messageService;
        _transportOptions = transportOptions;
        _active = active;
        _getSelectedRelayPeerId = getSelectedRelayPeerId;

        DisplayText = _model.DisplayName
            .Select(name => string.IsNullOrWhiteSpace(name) ? _model.PeerId.ToString()[..8] : name!)
            .ToBindableReactiveProperty(_model.PeerId.ToString()[..8])
            .AddTo(ref _bag);

        RuntimeStateText = _model.RuntimeState
            .Select(state => state.PendingCorrelationId is null
                ? state.UiState.ToString()
                : $"{state.UiState} ({state.PendingCorrelationId.Value.ToString()[..8]})")
            .ToBindableReactiveProperty(_model.RuntimeState.CurrentValue.UiState.ToString())
            .AddTo(ref _bag);

        var toggleOnline = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        toggleOnline.AsObservable()
            .Subscribe(_ => _model.SetOnline(!_model.IsOnline.CurrentValue))
            .AddTo(ref _bag);
        ToggleOnlineCommand = toggleOnline.AddTo(ref _bag);

        var toggleRelayCapable = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        toggleRelayCapable.AsObservable()
            .Subscribe(_ => _model.SetRelayCapable(!_model.IsRelayCapable.CurrentValue))
            .AddTo(ref _bag);
        ToggleRelayCapableCommand = toggleRelayCapable.AddTo(ref _bag);

        var remove = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        remove.AsObservable()
            .SubscribeAwait(async (_, ct) => await _directory.RemovePeerAsync(_model.PeerId, ct), AwaitOperation.Drop)
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
    }

    public Guid PeerId => _model.PeerId;

    public BindableReactiveProperty<string> DisplayText { get; }

    public BindableReactiveProperty<string> RuntimeStateText { get; }

    public ReactiveCommand<Unit> ToggleOnlineCommand { get; }
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

    private async Task ExecuteMainInviteDirectAsync(System.Threading.CancellationToken ct)
    {
        if (_active.Identity is null)
        {
            return;
        }

        var invite = _inviteFactory.CreateInvite();
        var inviterPeerId = _active.Identity.Id;

        var acceptance = await _peerRuntime.AcceptReverseSignalInviteAsync(
            simulatedPeerId: _model.PeerId,
            inviterPeerId: inviterPeerId,
            invite: invite,
            cancellationToken: ct).ConfigureAwait(false);

        _sessionToMain = acceptance.SessionId;

        await _peerRuntime.DeliverInviteHandshakeResponseToMainAsync(acceptance.Response, ct).ConfigureAwait(false);
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

        _relay.EnqueueToRelayHost(relayPeerId.Value, _model.PeerId, invite.ToByteArray(), debugType: nameof(EstablishDirectSessionRequest));

        var dequeued = _relay.FetchFromRelayHost(relayPeerId.Value, _model.PeerId, max: 1);
        if (dequeued.Count == 0)
        {
            return;
        }

        var req = EstablishDirectSessionRequest.Parser.ParseFrom(dequeued[0].OpaqueBytes);
        var inviterPeerId = _active.Identity.Id;

        var acceptance = await _peerRuntime.AcceptReverseSignalInviteAsync(
            simulatedPeerId: _model.PeerId,
            inviterPeerId: inviterPeerId,
            invite: req,
            cancellationToken: ct).ConfigureAwait(false);

        _relay.EnqueueToRelayHost(relayPeerId.Value, inviterPeerId, acceptance.Response.ToByteArray(), debugType: nameof(InviteHandshakeResponse));
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

        if (_model.PeerId != relayPeerId.Value)
        {
            return;
        }

        if (_sessionToMain is null)
        {
            return;
        }

        await _relay.ForwardQueuedToMainAsync(
            relayHostPeerId: relayPeerId.Value,
            recipientPeerId: _active.Identity.Id,
            relayHostToMainSessionId: _sessionToMain,
            cancellationToken: ct).ConfigureAwait(false);
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
        _relay.EnqueueToRelayHost(relayPeerId.Value, _active.Identity.Id, invite.ToByteArray(), debugType: nameof(EstablishDirectSessionRequest));
        return Task.CompletedTask;
    }

    private EstablishDirectSessionRequest CreatePeerToMainInvite()
    {
        var port = _transportOptions.Value.GrpcPort;
        if (port == 0) port = 5001;

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
        var preKeySig = identityEcdsa.SignData(inviterSignedPreKeySpki, HashAlgorithmName.SHA256);

        var correlation = Guid.NewGuid();
        var payload = new InviteHandshakeRequestPayload
        {
            Version = 1,
            InviterHost = "localhost",
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
