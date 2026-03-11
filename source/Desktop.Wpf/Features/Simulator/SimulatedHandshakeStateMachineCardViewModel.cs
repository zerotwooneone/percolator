using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;
using Percolator.Application.Configuration;
using Percolator.Contracts;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatedHandshakeStateMachineCardViewModel : IDisposable
{
    private static readonly Guid MainNodeSentinelPeerId = new("88880000-0000-0000-0000-000000000000");
    private readonly SimulatedPeerModel _model;
    private readonly ISimulatedPeerRuntimeService _runtime;
    private readonly ISimulatorRelayEmulator _relay;
    private readonly ISimulatorMainIngressService _mainIngress;
    private readonly ISimulatorDiagnosticsService _diagnostics;
    private readonly IOptions<TransportOptions> _transportOptions;
    private readonly Percolator.Application.Identity.ActiveIdentityContext _active;
    private readonly ISimulatorStateService _state;
    private readonly Func<Guid?> _selectedRelayHostPeerId;

    private DisposableBag _bag;

    public SimulatedHandshakeStateMachineCardViewModel(
        SimulatedPeerModel model,
        ISimulatedPeerRuntimeService runtime,
        ISimulatorRelayEmulator relay,
        ISimulatorMainIngressService mainIngress,
        ISimulatorDiagnosticsService diagnostics,
        IOptions<TransportOptions> transportOptions,
        Percolator.Application.Identity.ActiveIdentityContext active,
        ISimulatorStateService state,
        Func<Guid?> selectedRelayHostPeerId)
    {
        _model = model;
        _runtime = runtime;
        _relay = relay;
        _mainIngress = mainIngress;
        _diagnostics = diagnostics;
        _transportOptions = transportOptions;
        _active = active;
        _state = state;
        _selectedRelayHostPeerId = selectedRelayHostPeerId;

        DisplayName = _model.DisplayName
            .Select(n => string.IsNullOrWhiteSpace(n) ? _model.PeerId.ToString()[..8] : n!)
            .ToBindableReactiveProperty(_model.PeerId.ToString()[..8])
            .AddTo(ref _bag);

        StateText = _model.RuntimeState
            .Select(MapState)
            .ToBindableReactiveProperty("No Handshake")
            .AddTo(ref _bag);

        StateBadgeText = _model.RuntimeState
            .Select(s => $"STATE: {MapState(s).ToUpperInvariant()}" )
            .ToBindableReactiveProperty("STATE: NO HANDSHAKE")
            .AddTo(ref _bag);

        StateBadgeBackground = _model.RuntimeState
            .Select(MapBadgeBackground)
            .ToBindableReactiveProperty(Brushes.Transparent)
            .AddTo(ref _bag);

        ShowSendRequest = _model.RuntimeState
            .Select(s => s.UiState == SimulatorPeerUiState.Ready)
            .ToBindableReactiveProperty(true)
            .AddTo(ref _bag);

        ShowAccept = _model.RuntimeState
            .Select(s => s.UiState == SimulatorPeerUiState.InboundPending)
            .ToBindableReactiveProperty(false)
            .AddTo(ref _bag);

        ShowForceExpire = _model.RuntimeState
            .Select(s => s.UiState == SimulatorPeerUiState.OutboundPending)
            .ToBindableReactiveProperty(false)
            .AddTo(ref _bag);

        ShowReset = _model.RuntimeState
            .Select(s => s.UiState == SimulatorPeerUiState.Established || s.UiState == SimulatorPeerUiState.Expired)
            .ToBindableReactiveProperty(false)
            .AddTo(ref _bag);

        var send = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        send.AsObservable().SubscribeAwait(async (_, ct) => await ExecuteSendRequestToMainAsync(ct), AwaitOperation.Drop).AddTo(ref _bag);
        SendRequestToMainCommand = send.AddTo(ref _bag);

        var sendRelayed = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        sendRelayed.AsObservable().SubscribeAwait(async (_, ct) => await ExecuteSendRelayedRequestToMainAsync(ct), AwaitOperation.Drop).AddTo(ref _bag);
        SendRelayedRequestToMainCommand = sendRelayed.AddTo(ref _bag);

        var accept = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        accept.AsObservable().SubscribeAwait(async (_, ct) => await ExecuteAcceptHandshakeAsync(ct), AwaitOperation.Drop).AddTo(ref _bag);
        AcceptHandshakeCommand = accept.AddTo(ref _bag);

        ForceExpireCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => ExecuteForceExpire())
            .AddTo(ref _bag);

        ResetStateCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => ExecuteReset())
            .AddTo(ref _bag);

        // Nice-to-have toggles: surfaced on UI later.
        AutoAccept = new BindableReactiveProperty<bool>(false).AddTo(ref _bag);
        AutoRespond = new BindableReactiveProperty<bool>(false).AddTo(ref _bag);
    }

    public Guid PeerId => _model.PeerId;

    public BindableReactiveProperty<string> DisplayName { get; }

    public BindableReactiveProperty<string> StateText { get; }

    public BindableReactiveProperty<string> StateBadgeText { get; }

    public BindableReactiveProperty<Brush> StateBadgeBackground { get; }

    public BindableReactiveProperty<bool> ShowSendRequest { get; }

    public BindableReactiveProperty<bool> ShowAccept { get; }

    public BindableReactiveProperty<bool> ShowForceExpire { get; }

    public BindableReactiveProperty<bool> ShowReset { get; }

    public BindableReactiveProperty<bool> AutoAccept { get; }

    public BindableReactiveProperty<bool> AutoRespond { get; }

    public ReactiveCommand<Unit> SendRequestToMainCommand { get; }

    public ReactiveCommand<Unit> SendRelayedRequestToMainCommand { get; }

    public ReactiveCommand<Unit> AcceptHandshakeCommand { get; }

    public ReactiveCommand<Unit> ForceExpireCommand { get; }

    public ReactiveCommand<Unit> ResetStateCommand { get; }

    private async Task ExecuteSendRequestToMainAsync(CancellationToken ct)
    {
        if (_active.Identity is null) return;

        var invite = CreatePeerToMainInvite();

        // Mark state as request sent (outbound pending) based on correlation id in payload.
        if (invite.HasPayload && invite.Payload.Length > 0)
        {
            try
            {
                var payload = InviteHandshakeRequestPayload.Parser.ParseFrom(invite.Payload);
                if (!string.IsNullOrWhiteSpace(payload.RequestCorrelationId) && Guid.TryParse(payload.RequestCorrelationId, out var corr))
                {
                    _model.MarkOutboundPending(corr);
                    _diagnostics.Emit(
                        SimulatorDiagnosticEventType.HandshakeStateTransition,
                        $"Handshake: outbound pending corr={corr.ToString()[..8]}",
                        peerId: _model.PeerId,
                        contextTag: "OutboundPending");
                }
                else
                {
                    var corr2 = Guid.NewGuid();
                    _model.MarkOutboundPending(corr2);
                    _diagnostics.Emit(
                        SimulatorDiagnosticEventType.HandshakeStateTransition,
                        $"Handshake: outbound pending corr={corr2.ToString()[..8]}",
                        peerId: _model.PeerId,
                        contextTag: "OutboundPending");
                }
            }
            catch
            {
                var corr3 = Guid.NewGuid();
                _model.MarkOutboundPending(corr3);
                _diagnostics.Emit(
                    SimulatorDiagnosticEventType.HandshakeStateTransition,
                    $"Handshake: outbound pending corr={corr3.ToString()[..8]}",
                    peerId: _model.PeerId,
                    contextTag: "OutboundPending");
            }
        }
        else
        {
            var corr4 = Guid.NewGuid();
            _model.MarkOutboundPending(corr4);
            _diagnostics.Emit(
                SimulatorDiagnosticEventType.HandshakeStateTransition,
                $"Handshake: outbound pending corr={corr4.ToString()[..8]}",
                peerId: _model.PeerId,
                contextTag: "OutboundPending");
        }

        await _mainIngress
            .SendEstablishDirectSessionToMainAsync(invite, ct)
            .ConfigureAwait(false);
    }

    private async Task ExecuteSendRelayedRequestToMainAsync(CancellationToken ct)
    {
        if (_active.Identity is null) return;

        var relayHostPeerId = _selectedRelayHostPeerId();
        var relayHost = relayHostPeerId.HasValue
            ? _state.Peers.FirstOrDefault(p => p.PeerId == relayHostPeerId.Value)
            : null;

        relayHost ??= _state.Peers.FirstOrDefault(p => p.Relay?.IsRelayCapable == true);
        if (relayHost is null) return;

        var invite = CreatePeerToMainInvite();

        await _relay.EnqueueToRelayHostAsync(
                relayHostPeerId: relayHost.PeerId,
                recipientPeerId: MainNodeSentinelPeerId,
                opaqueBytes: invite.ToByteArray(),
                debugType: nameof(EstablishDirectSessionRequest),
                cancellationToken: ct)
            .ConfigureAwait(false);
    }

    private async Task ExecuteAcceptHandshakeAsync(CancellationToken ct)
    {
        var corr = _model.RuntimeState.CurrentValue.PendingCorrelationId;
        if (corr is null) return;
        if (_active.Identity is null) return;

        var finalized = await _runtime.TryFinalizeInviteHandshakeResponseFromMainAsync(
                simulatedPeerId: _model.PeerId,
                acceptorPeerId: MainNodeSentinelPeerId,
                requestCorrelationId: corr.Value,
                cancellationToken: ct)
            .ConfigureAwait(false);

        if (finalized is null)
        {
            return;
        }

        _model.MarkEstablished();
        _diagnostics.Emit(
            SimulatorDiagnosticEventType.HandshakeStateTransition,
            $"Handshake: established corr={corr.Value.ToString()[..8]}",
            peerId: _model.PeerId,
            contextTag: "Established");
    }

    private void ExecuteForceExpire()
    {
        _model.MarkExpired();
        _diagnostics.Emit(
            SimulatorDiagnosticEventType.HandshakeStateTransition,
            "Handshake: expired",
            peerId: _model.PeerId,
            contextTag: "Expired");
    }

    private void ExecuteReset()
    {
        _model.ClearRuntimeState();
        _diagnostics.Emit(
            SimulatorDiagnosticEventType.HandshakeStateTransition,
            "Handshake: reset (no handshake)",
            peerId: _model.PeerId,
            contextTag: "NoHandshake");
    }

    private EstablishDirectSessionRequest CreatePeerToMainInvite()
    {
        // Simulated peer inviter must advertise its simulator endpoint so the main app can route responses back in-process.
        var port = _transportOptions.Value.SimulatorPort;
        if (port == 0) port = 5002;

        var inviterHost = AllocateSimulatorLoopbackHost(_model.PeerId);

        using var identityEcdh = ECDiffieHellman.Create();
        identityEcdh.ImportECPrivateKey(_model.IdentitySigningKeyPrivateKeyEcPrivateKey, out _);
        using var identityEcdsa = ECDsa.Create(identityEcdh.ExportParameters(true));

        var curve = identityEcdh.ExportParameters(false).Curve;
        using var inviterSignedPreKey = ECDiffieHellman.Create(curve);
        var inviterSignedPreKeySpki = inviterSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var inviterSignedPreKeyPriv = inviterSignedPreKey.ExportECPrivateKey();
        var preKeySig = identityEcdsa.SignData(inviterSignedPreKeySpki, HashAlgorithmName.SHA256);

        var correlation = Guid.NewGuid();
        _runtime.RecordOutboundInviteSignedPreKeyPrivate(_model.PeerId, correlation, inviterSignedPreKeyPriv);

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

    private static string AllocateSimulatorLoopbackHost(Guid peerId)
    {
        // Stable mapping of Guid -> 127.77.X.Y. Keep within 1..254 to avoid network/broadcast edge cases.
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(peerId.ToByteArray());
        var x = (byte)((hash[0] % 254) + 1);
        var y = (byte)((hash[1] % 254) + 1);
        return $"127.77.{x}.{y}";
    }

    private static string MapState(SimulatorPeerRuntimeState state)
    {
        return state.UiState switch
        {
            SimulatorPeerUiState.Ready => "No Handshake",
            SimulatorPeerUiState.OutboundPending => "Request Sent",
            SimulatorPeerUiState.InboundPending => "Request Received",
            SimulatorPeerUiState.Established => "Handshake Complete",
            SimulatorPeerUiState.Expired => "Expired",
            SimulatorPeerUiState.Offline => "Offline",
            _ => state.UiState.ToString()
        };
    }

    private static Brush MapBadgeBackground(SimulatorPeerRuntimeState state)
    {
        return state.UiState switch
        {
            SimulatorPeerUiState.Ready => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333")),
            SimulatorPeerUiState.OutboundPending => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3A2A05")),
            SimulatorPeerUiState.InboundPending => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2B1646")),
            SimulatorPeerUiState.Established => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#063A26")),
            SimulatorPeerUiState.Expired => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3A0505")),
            _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"))
        };
    }

    public void Dispose()
    {
        _bag.Dispose();
        DisplayName.Dispose();
        StateText.Dispose();
        StateBadgeText.Dispose();
        StateBadgeBackground.Dispose();
        ShowSendRequest.Dispose();
        ShowAccept.Dispose();
        ShowForceExpire.Dispose();
        ShowReset.Dispose();
        AutoAccept.Dispose();
        AutoRespond.Dispose();
    }
}
