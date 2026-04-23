using System.Security.Cryptography;
using System.Windows;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;
using ObservableCollections;
using Percolator.Application.Configuration;
using Percolator.Contracts;
using Percolator.Network;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatedHandshakeStateMachineCardViewModel : IDisposable
{
    private readonly SimulatedPeerModel _model;
    private readonly ISimulatorStateService _state;
    private readonly ISimulatorMainIngressService _mainIngress;
    private readonly ISimulatorDiagnosticsService _diagnostics;
    private readonly IOptions<TransportOptions> _transportOptions;
    private readonly Percolator.Application.Identity.ActiveIdentityContext _active;
    private readonly Func<PeerId?> _selectedRelayHostPeerId;

    private DisposableBag _bag;

    public SimulatedHandshakeStateMachineCardViewModel(
        SimulatedPeerModel model,
        ISimulatorStateService state,
        ISimulatorMainIngressService mainIngress,
        ISimulatorDiagnosticsService diagnostics,
        IOptions<TransportOptions> transportOptions,
        Percolator.Application.Identity.ActiveIdentityContext active,
        Func<PeerId?> selectedRelayHostPeerId)
    {
        _model = model;
        _state = state;
        _mainIngress = mainIngress;
        _diagnostics = diagnostics;
        _transportOptions = transportOptions;
        _active = active;
        _selectedRelayHostPeerId = selectedRelayHostPeerId;

        DisplayName = _model.DisplayName
            .ObserveOnCurrentSynchronizationContext()
            .Select(n => string.IsNullOrWhiteSpace(n) ? _model.PeerId.ToString()[..8] : n!)
            .ToBindableReactiveProperty(_model.PeerId.ToString()[..8])
            .AddTo(ref _bag);

        StateText = _model.UiState
            .ObserveOnCurrentSynchronizationContext()
            .Select(MapState)
            .ToBindableReactiveProperty("No Handshake")
            .AddTo(ref _bag);

        UiState = _model.UiState
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty(SimulatorPeerUiState.Ready)
            .AddTo(ref _bag);

        StateBadgeText = _model.UiState
            .ObserveOnCurrentSynchronizationContext()
            .Select(s => $"STATE: {MapState(s).ToUpperInvariant()}" )
            .ToBindableReactiveProperty("STATE: NO HANDSHAKE")
            .AddTo(ref _bag);

        ShowSendRequest = _model.UiState
            .ObserveOnCurrentSynchronizationContext()
            .Select(s => s == SimulatorPeerUiState.Ready)
            .ToBindableReactiveProperty(true)
            .AddTo(ref _bag);

        ShowAccept = _model.UiState
            .ObserveOnCurrentSynchronizationContext()
            .Select(s => s == SimulatorPeerUiState.AwaitingUserAcceptance)
            .ToBindableReactiveProperty(false)
            .AddTo(ref _bag);

        ShowAcceptReverseSignal = Observable
            .CombineLatest(
                _model.UiState,
                _model.InboundReverseSignalPendingCorrelationId,
                static (s, corr) => s == SimulatorPeerUiState.AwaitingUserAcceptance && corr is not null)
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty(false)
            .AddTo(ref _bag);

        PendingStandardSignalHellos = _model.PendingInboundStandardSignalHellosMutable
            .ObserveChanged()
            .ObserveOnCurrentSynchronizationContext()
            .Select(_ => (IReadOnlyList<PendingStandardSignalHelloItem>)_model.PendingInboundStandardSignalHellos
                .Select(static kvp => new PendingStandardSignalHelloItem(kvp.Key, kvp.Value.ReceivedUtc))
                .OrderByDescending(static x => x.ReceivedUtc)
                .ToArray())
            .ToBindableReactiveProperty(Array.Empty<PendingStandardSignalHelloItem>())
            .AddTo(ref _bag);

        ShowForceExpire = _model.UiState
            .ObserveOnCurrentSynchronizationContext()
            .Select(s => s == SimulatorPeerUiState.OutboundPending)
            .ToBindableReactiveProperty(false)
            .AddTo(ref _bag);

        ShowReset = _model.UiState
            .ObserveOnCurrentSynchronizationContext()
            .Select(s => s == SimulatorPeerUiState.Established || s == SimulatorPeerUiState.Expired)
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

        var acceptStandard = Observable.Return(true).ToReactiveCommand<string>(_ => { });
        acceptStandard.AsObservable().SubscribeAwait(async (initiatorPkhHex, ct) => await ExecuteAcceptPendingStandardSignalHelloAsync(initiatorPkhHex, ct), AwaitOperation.Drop).AddTo(ref _bag);
        AcceptPendingStandardSignalHelloCommand = acceptStandard.AddTo(ref _bag);

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

    public PeerId PeerId => _model.PeerId;

    public BindableReactiveProperty<string> DisplayName { get; }

    public BindableReactiveProperty<string> StateText { get; }

    public BindableReactiveProperty<string> StateBadgeText { get; }

    public BindableReactiveProperty<SimulatorPeerUiState> UiState { get; }

    public BindableReactiveProperty<bool> ShowSendRequest { get; }

    public BindableReactiveProperty<bool> ShowAccept { get; }

    public BindableReactiveProperty<bool> ShowAcceptReverseSignal { get; }

    public BindableReactiveProperty<IReadOnlyList<PendingStandardSignalHelloItem>> PendingStandardSignalHellos { get; }

    public BindableReactiveProperty<bool> ShowForceExpire { get; }

    public BindableReactiveProperty<bool> ShowReset { get; }

    public BindableReactiveProperty<bool> AutoAccept { get; }

    public BindableReactiveProperty<bool> AutoRespond { get; }

    public ReactiveCommand<Unit> SendRequestToMainCommand { get; }

    public ReactiveCommand<Unit> SendRelayedRequestToMainCommand { get; }

    public ReactiveCommand<Unit> AcceptHandshakeCommand { get; }

    public ReactiveCommand<string> AcceptPendingStandardSignalHelloCommand { get; }

    public ReactiveCommand<Unit> ForceExpireCommand { get; }

    public ReactiveCommand<Unit> ResetStateCommand { get; }

    public sealed record PendingStandardSignalHelloItem(string InitiatorPkhHex, DateTimeOffset ReceivedUtc);

    private async Task ExecuteSendRequestToMainAsync(CancellationToken ct)
    {
        if (_active.Identity is null) return;

        var invite = CreatePeerToMainInvite();

        await InvokeOnUiAsync(() =>
        {
            _model.SetSelectedRouteMode(ConnectionMode.Direct);
            _model.SetRelayHostPeerId(null);
            _model.SetPhase("InviteSent");
        }).ConfigureAwait(false);

        // Mark state as request sent (outbound pending) based on correlation id in payload.
        if (invite.HasPayload && invite.Payload.Length > 0)
        {
            try
            {
                var payload = InviteHandshakeRequestPayload.Parser.ParseFrom(invite.Payload);
                if (!string.IsNullOrWhiteSpace(payload.RequestCorrelationId) && Guid.TryParse(payload.RequestCorrelationId, out var corr))
                {
                    await InvokeOnUiAsync(() =>
                    {
                        _model.MarkOutboundPending(corr);
                        _model.SetAttemptPhase(corr, "InviteSent");
                        _model.SetAttemptError(corr, null);
                    }).ConfigureAwait(false);
                    _diagnostics.Emit(
                        SimulatorDiagnosticEventType.HandshakeStateTransition,
                        $"Handshake: outbound pending corr={corr.ToString()[..8]}",
                        peerId: _model.PeerId,
                        contextTag: "OutboundPending");
                }
                else
                {
                    var corr2 = Guid.NewGuid();
                    await InvokeOnUiAsync(() =>
                    {
                        _model.MarkOutboundPending(corr2);
                        _model.SetAttemptPhase(corr2, "InviteSent");
                        _model.SetAttemptError(corr2, null);
                    }).ConfigureAwait(false);
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
                await InvokeOnUiAsync(() =>
                {
                    _model.MarkOutboundPending(corr3);
                    _model.SetAttemptPhase(corr3, "InviteSent");
                    _model.SetAttemptError(corr3, null);
                }).ConfigureAwait(false);
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
            await InvokeOnUiAsync(() =>
            {
                _model.MarkOutboundPending(corr4);
                _model.SetAttemptPhase(corr4, "InviteSent");
                _model.SetAttemptError(corr4, null);
            }).ConfigureAwait(false);
            _diagnostics.Emit(
                SimulatorDiagnosticEventType.HandshakeStateTransition,
                $"Handshake: outbound pending corr={corr4.ToString()[..8]}",
                peerId: _model.PeerId,
                contextTag: "OutboundPending");
        }

        try
        {
            await _mainIngress
                .SendEstablishDirectSessionToMainAsync(invite, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var corr = _model.InboundReverseSignalPendingCorrelationId.CurrentValue;
            if (corr is not null)
            {
                await InvokeOnUiAsync(() =>
                {
                    _model.SetAttemptError(corr.Value, ex.Message);
                    _model.SetAttemptPhase(corr.Value, "SendFailed");
                }).ConfigureAwait(false);
            }
            throw;
        }
    }

    private async Task ExecuteSendRelayedRequestToMainAsync(CancellationToken ct)
    {
        if (_active.Identity is null) return;

        var relayHostPeerId = _selectedRelayHostPeerId();
        var relayHost = relayHostPeerId is not null
            ? _state.Peers.FirstOrDefault(p => p.PeerId == relayHostPeerId)
            : null;

        relayHost ??= _state.Peers.FirstOrDefault(p => p.IsRelayCapable.CurrentValue);
        if (relayHost is null) return;

        var invite = CreatePeerToMainInvite();

        await InvokeOnUiAsync(() =>
        {
            _model.SetSelectedRouteMode(ConnectionMode.ViaRelay);
            _model.SetRelayHostPeerId(new PeerId(relayHost.PeerId.Value));
            _model.SetPhase("InviteEnqueued");
        }).ConfigureAwait(false);

        // Mark state as request sent (outbound pending) based on correlation id in payload.
        if (invite.HasPayload && invite.Payload.Length > 0)
        {
            try
            {
                var payload = InviteHandshakeRequestPayload.Parser.ParseFrom(invite.Payload);
                if (!string.IsNullOrWhiteSpace(payload.RequestCorrelationId) && Guid.TryParse(payload.RequestCorrelationId, out var corr))
                {
                    await InvokeOnUiAsync(() =>
                    {
                        _model.MarkOutboundPending(corr);
                        _model.SetAttemptPhase(corr, "InviteEnqueued");
                        _model.SetAttemptError(corr, null);
                    }).ConfigureAwait(false);
                    _diagnostics.Emit(
                        SimulatorDiagnosticEventType.HandshakeStateTransition,
                        $"Handshake: outbound pending corr={corr.ToString()[..8]}",
                        peerId: _model.PeerId,
                        contextTag: "OutboundPending");
                }
                else
                {
                    var corr2 = Guid.NewGuid();
                    await InvokeOnUiAsync(() =>
                    {
                        _model.MarkOutboundPending(corr2);
                        _model.SetAttemptPhase(corr2, "InviteEnqueued");
                        _model.SetAttemptError(corr2, null);
                    }).ConfigureAwait(false);
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
                await InvokeOnUiAsync(() =>
                {
                    _model.MarkOutboundPending(corr3);
                    _model.SetAttemptPhase(corr3, "InviteEnqueued");
                    _model.SetAttemptError(corr3, null);
                }).ConfigureAwait(false);
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
            await InvokeOnUiAsync(() =>
            {
                _model.MarkOutboundPending(corr4);
                _model.SetAttemptPhase(corr4, "InviteEnqueued");
                _model.SetAttemptError(corr4, null);
            }).ConfigureAwait(false);
            _diagnostics.Emit(
                SimulatorDiagnosticEventType.HandshakeStateTransition,
                $"Handshake: outbound pending corr={corr4.ToString()[..8]}",
                peerId: _model.PeerId,
                contextTag: "OutboundPending");
        }

        try
        {
            await _state.EnqueueRelayUpstreamToMainAsync(
                    relayHostPeerId: relayHost.PeerId,
                    opaqueBytes: invite.ToByteArray(),
                    debugType: nameof(EstablishDirectSessionRequest),
                    cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var corr = _model.InboundReverseSignalPendingCorrelationId.CurrentValue;
            if (corr is not null)
            {
                await InvokeOnUiAsync(() =>
                {
                    _model.SetAttemptError(corr.Value, ex.Message);
                    _model.SetAttemptPhase(corr.Value, "RelayEnqueueFailed");
                }).ConfigureAwait(false);
            }
            throw;
        }
    }

    private async Task ExecuteAcceptHandshakeAsync(CancellationToken ct)
    {
        var corr = _model.InboundReverseSignalPendingCorrelationId.CurrentValue;
        if (corr is null) return;
        if (_active.Identity is null) return;

        var acceptorPeerId = _active.Identity is not null ? new PeerId(_active.Identity.Id) : new PeerId(Guid.Empty);
        var finalized = await _state.TryFinalizeInviteHandshakeResponseFromMainAsync(
                simulatedPeerId: _model.PeerId,
                acceptorPeerId: acceptorPeerId,
                requestCorrelationId: corr.Value,
                cancellationToken: ct)
            .ConfigureAwait(false);

        // Chunk H.2: If finalization fails, this pending was likely initiated by Main -> Simulator.
        // In that case we need to deliver the queued InviteHandshakeResponse to Main, and the
        // simulated peer can transition to Established immediately (it already created the session
        // during invite receipt).
        if (finalized is null)
        {
            var delivered = await _state.TryDeliverQueuedInviteHandshakeResponseToMainAsync(
                    simulatedPeerId: _model.PeerId,
                    requestCorrelationId: corr.Value,
                    cancellationToken: ct)
                .ConfigureAwait(false);

            if (!delivered)
            {
                return;
            }
        }

        await InvokeOnUiAsync(() => _model.MarkEstablished()).ConfigureAwait(false);
        _diagnostics.Emit(
            SimulatorDiagnosticEventType.HandshakeStateTransition,
            $"Handshake: established corr={corr.Value.ToString()[..8]}",
            peerId: _model.PeerId,
            contextTag: "Established");
    }

    private async Task ExecuteAcceptPendingStandardSignalHelloAsync(string initiatorPkhHex, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(initiatorPkhHex)) return;

        var accepted = await _state
            .TryAcceptPendingStandardSignalHelloAsync(
                recipientPeerId: _model.PeerId,
                initiatorPkhHex: initiatorPkhHex,
                cancellationToken: ct)
            .ConfigureAwait(false);

        if (!accepted)
        {
            _diagnostics.Emit(
                SimulatorDiagnosticEventType.HandshakeStateTransition,
                $"Handshake: standard-signal accept failed initiator={initiatorPkhHex}",
                peerId: _model.PeerId,
                contextTag: "StandardSignalAcceptFailed");
        }
    }

    private void ExecuteForceExpire()
    {
        _ = InvokeOnUiAsync(() => _model.MarkExpired());
        _diagnostics.Emit(
            SimulatorDiagnosticEventType.HandshakeStateTransition,
            "Handshake: expired",
            peerId: _model.PeerId,
            contextTag: "Expired");
    }

    private void ExecuteReset()
    {
        _ = InvokeOnUiAsync(() => _model.ClearRuntimeState());
        _diagnostics.Emit(
            SimulatorDiagnosticEventType.HandshakeStateTransition,
            "Handshake: reset (no handshake)",
            peerId: _model.PeerId,
            contextTag: "NoHandshake");
    }

    private static Task InvokeOnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
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

    private static string AllocateSimulatorLoopbackHost(PeerId peerId)
    {
        // Stable mapping of Guid -> 127.77.X.Y. Keep within 1..254 to avoid network/broadcast edge cases.
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(peerId.Value.ToByteArray());
        var x = (byte)((hash[0] % 254) + 1);
        var y = (byte)((hash[1] % 254) + 1);
        return $"127.77.{x}.{y}";
    }

    private static string MapState(SimulatorPeerUiState state)
    {
        return state switch
        {
            SimulatorPeerUiState.Ready => "No Handshake",
            SimulatorPeerUiState.OutboundPending => "Request Sent",
            SimulatorPeerUiState.AwaitingUserAcceptance => "Request Received",
            SimulatorPeerUiState.Established => "Handshake Complete",
            SimulatorPeerUiState.Expired => "Expired",
            SimulatorPeerUiState.Offline => "Offline",
            _ => state.ToString()
        };
    }

    public void Dispose()
    {
        _bag.Dispose();
        DisplayName.Dispose();
        StateText.Dispose();
        StateBadgeText.Dispose();
        UiState.Dispose();
        ShowSendRequest.Dispose();
        ShowAccept.Dispose();
        ShowForceExpire.Dispose();
        ShowReset.Dispose();
        AutoAccept.Dispose();
        AutoRespond.Dispose();
    }
}
