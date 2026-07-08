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
    private readonly ISimulatorToMainTransportService _mainIngress;
    private readonly ISimulatorDiagnosticsService _diagnostics;
    private readonly IOptions<TransportOptions> _transportOptions;
    private readonly Percolator.Application.Identity.ActiveIdentityContext _active;
    private readonly Func<NetworkPeerId?> _selectedRelayHostPeerId;

    private DisposableBag _bag;

    public SimulatedHandshakeStateMachineCardViewModel(
        SimulatedPeerModel model,
        ISimulatorStateService state,
        ISimulatorToMainTransportService mainIngress,
        ISimulatorDiagnosticsService diagnostics,
        IOptions<TransportOptions> transportOptions,
        Percolator.Application.Identity.ActiveIdentityContext active,
        Func<NetworkPeerId?> selectedRelayHostPeerId)
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
            .Select(n => string.IsNullOrWhiteSpace(n) ? _model.NetworkPeerId.ToString()[..8] : n!)
            .ToBindableReactiveProperty(_model.NetworkPeerId.ToString()[..8])
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

        ShowAcceptReverseSignal = _model.UiState
            .Select(s => s == SimulatorPeerUiState.AwaitingUserAcceptance)
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

        PendingApprovals = Observable
            .Merge(
                _model.PendingInboundDirectInvites.ObserveChanged().Select(_ => Unit.Default),
                _model.PendingInboundStandardSignalHellosMutable.ObserveChanged().Select(_ => Unit.Default))
            .ObserveOnCurrentSynchronizationContext()
            .Select(_ =>
            {
                var items = new List<PendingApprovalItem>();

                // Reverse-signal inbound direct invite requests from Main
                foreach (var invite in _model.PendingInboundDirectInvites)
                {
                    items.Add(new PendingApprovalItem(
                        invite.CorrelationId,
                        PendingHandshakeKind.InboundDirectInviteRequestFromMain,
                        invite.ReceivedAtUtc,
                        $"Direct Invite from Main (corr: {invite.CorrelationId.ToString()[..8]})"));
                }

                // Standard-signal inbound hellos from peers
                foreach (var kvp in _model.PendingInboundStandardSignalHellos)
                {
                    var corr = Guid.TryParse(kvp.Key, out var g) ? g : Guid.Empty;
                    items.Add(new PendingApprovalItem(
                        corr,
                        PendingHandshakeKind.InboundStandardSignalHello,
                        kvp.Value.ReceivedUtc,
                        $"Standard Signal Hello from {kvp.Key[..8]}"));
                }

                return (IReadOnlyList<PendingApprovalItem>)items
                    .OrderByDescending(x => x.ReceivedUtc)
                    .ToArray();
            })
            .ToBindableReactiveProperty(Array.Empty<PendingApprovalItem>())
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

        var acceptStandard = Observable.Return(true).ToReactiveCommand<string>(_ => { });
        acceptStandard.AsObservable().SubscribeAwait(async (initiatorPkhHex, ct) => await ExecuteAcceptPendingStandardSignalHelloAsync(initiatorPkhHex, ct), AwaitOperation.Drop).AddTo(ref _bag);
        AcceptPendingStandardSignalHelloCommand = acceptStandard.AddTo(ref _bag);

        var acceptPending = Observable.Return(true).ToReactiveCommand<PendingApprovalItem>(_ => { });
        acceptPending.AsObservable().SubscribeAwait(async (item, ct) => await ExecuteAcceptPendingApprovalAsync(item, ct), AwaitOperation.Drop).AddTo(ref _bag);
        AcceptPendingApprovalCommand = acceptPending.AddTo(ref _bag);

        var rejectPending = Observable.Return(true).ToReactiveCommand<PendingApprovalItem>(_ => { });
        rejectPending.AsObservable().SubscribeAwait(async (item, ct) => await ExecuteRejectPendingApprovalAsync(item, ct), AwaitOperation.Drop).AddTo(ref _bag);
        RejectPendingApprovalCommand = rejectPending.AddTo(ref _bag);

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

    public NetworkPeerId NetworkPeerId => _model.NetworkPeerId;

    public BindableReactiveProperty<string> DisplayName { get; }

    public BindableReactiveProperty<string> StateText { get; }

    public BindableReactiveProperty<string> StateBadgeText { get; }

    public BindableReactiveProperty<SimulatorPeerUiState> UiState { get; }

    public BindableReactiveProperty<bool> ShowSendRequest { get; }

    public BindableReactiveProperty<bool> ShowAccept { get; }

    public BindableReactiveProperty<bool> ShowAcceptReverseSignal { get; }

    public BindableReactiveProperty<IReadOnlyList<PendingStandardSignalHelloItem>> PendingStandardSignalHellos { get; }

    public BindableReactiveProperty<IReadOnlyList<PendingApprovalItem>> PendingApprovals { get; }

    public BindableReactiveProperty<bool> ShowForceExpire { get; }

    public BindableReactiveProperty<bool> ShowReset { get; }

    public BindableReactiveProperty<bool> AutoAccept { get; }

    public BindableReactiveProperty<bool> AutoRespond { get; }

    public ReactiveCommand<Unit> SendRequestToMainCommand { get; }

    public ReactiveCommand<Unit> SendRelayedRequestToMainCommand { get; }

    public ReactiveCommand<string> AcceptPendingStandardSignalHelloCommand { get; }

    public ReactiveCommand<PendingApprovalItem> AcceptPendingApprovalCommand { get; }

    public ReactiveCommand<PendingApprovalItem> RejectPendingApprovalCommand { get; }

    public ReactiveCommand<Unit> ForceExpireCommand { get; }

    public ReactiveCommand<Unit> ResetStateCommand { get; }

    public sealed record PendingStandardSignalHelloItem(string InitiatorPkhHex, DateTimeOffset ReceivedUtc);

    public enum PendingHandshakeKind
    {
        InboundDirectInviteRequestFromMain,
        InboundStandardSignalHello
    }

    public sealed record PendingApprovalItem(
        Guid CorrelationId,
        PendingHandshakeKind Kind,
        DateTimeOffset ReceivedUtc,
        string DisplayText);

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
                        peerId: _model.NetworkPeerId,
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
                        peerId: _model.NetworkPeerId,
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
                    peerId: _model.NetworkPeerId,
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
                peerId: _model.NetworkPeerId,
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
            // Use the correlation id from the invite payload that was just generated
            if (invite.HasPayload && invite.Payload.Length > 0)
            {
                try
                {
                    var payload = InviteHandshakeRequestPayload.Parser.ParseFrom(invite.Payload);
                    if (!string.IsNullOrWhiteSpace(payload.RequestCorrelationId) && Guid.TryParse(payload.RequestCorrelationId, out var corr))
                    {
                        await InvokeOnUiAsync(() =>
                        {
                            _model.SetAttemptError(corr, ex.Message);
                            _model.SetAttemptPhase(corr, "SendFailed");
                        }).ConfigureAwait(false);
                    }
                }
                catch { /* If parsing fails, we can't set error on a specific correlation id */ }
            }
            throw;
        }
    }

    private async Task ExecuteSendRelayedRequestToMainAsync(CancellationToken ct)
    {
        if (_active.Identity is null) return;

        var relayHostPeerId = _selectedRelayHostPeerId();
        var relayHost = relayHostPeerId is not null
            ? _state.Peers.FirstOrDefault(p => p.NetworkPeerId == relayHostPeerId)
            : null;

        relayHost ??= _state.Peers.FirstOrDefault(p => p.IsRelayCapable.CurrentValue);
        if (relayHost is null) return;

        var invite = CreatePeerToMainInvite();

        await InvokeOnUiAsync(() =>
        {
            _model.SetSelectedRouteMode(ConnectionMode.ViaRelay);
            _model.SetRelayHostPeerId(relayHost.NetworkPeerId);
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
                        peerId: _model.NetworkPeerId,
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
                        peerId: _model.NetworkPeerId,
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
                    peerId: _model.NetworkPeerId,
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
                peerId: _model.NetworkPeerId,
                contextTag: "OutboundPending");
        }

        try
        {
            await _state.EnqueueRelayUpstreamToMainAsync(
                    relayHostNetworkPeerId: relayHost.NetworkPeerId,
                    opaqueBytes: invite.ToByteArray(),
                    debugType: nameof(EstablishDirectSessionRequest),
                    cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Use the correlation id from the invite payload that was just generated
            if (invite.HasPayload && invite.Payload.Length > 0)
            {
                try
                {
                    var payload = InviteHandshakeRequestPayload.Parser.ParseFrom(invite.Payload);
                    if (!string.IsNullOrWhiteSpace(payload.RequestCorrelationId) && Guid.TryParse(payload.RequestCorrelationId, out var corr))
                    {
                        await InvokeOnUiAsync(() =>
                        {
                            _model.SetAttemptError(corr, ex.Message);
                            _model.SetAttemptPhase(corr, "RelayEnqueueFailed");
                        }).ConfigureAwait(false);
                    }
                }
                catch { /* If parsing fails, we can't set error on a specific correlation id */ }
            }
            throw;
        }
    }

    private async Task ExecuteAcceptPendingStandardSignalHelloAsync(string initiatorPkhHex, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(initiatorPkhHex)) return;

        var accepted = await _state
            .TryAcceptPendingStandardSignalHelloAsync(
                recipientNetworkPeerId: _model.NetworkPeerId,
                initiatorPkhHex: initiatorPkhHex,
                cancellationToken: ct)
            .ConfigureAwait(false);

        if (!accepted)
        {
            _diagnostics.Emit(
                SimulatorDiagnosticEventType.HandshakeStateTransition,
                $"Handshake: standard-signal accept failed initiator={initiatorPkhHex}",
                peerId: _model.NetworkPeerId,
                contextTag: "StandardSignalAcceptFailed");
        }
    }

    private async Task ExecuteAcceptPendingApprovalAsync(PendingApprovalItem item, CancellationToken ct)
    {
        if (item is null) return;

        switch (item.Kind)
        {
            case PendingHandshakeKind.InboundDirectInviteRequestFromMain:
                await _state.AcceptPendingInboundDirectInviteAsync(
                    simulatedNetworkPeerId: _model.NetworkPeerId,
                    correlationId: item.CorrelationId,
                    cancellationToken: ct)
                    .ConfigureAwait(false);
                break;

            case PendingHandshakeKind.InboundStandardSignalHello:
                var accepted = await _state
                    .TryAcceptPendingStandardSignalHelloAsync(
                        recipientNetworkPeerId: _model.NetworkPeerId,
                        initiatorPkhHex: item.CorrelationId.ToString(),
                        cancellationToken: ct)
                    .ConfigureAwait(false);

                if (!accepted)
                {
                    _diagnostics.Emit(
                        SimulatorDiagnosticEventType.HandshakeStateTransition,
                        $"Handshake: standard-signal accept failed corr={item.CorrelationId}",
                        peerId: _model.NetworkPeerId,
                        contextTag: "StandardSignalAcceptFailed");
                }
                break;

            default:
                _diagnostics.Emit(
                    SimulatorDiagnosticEventType.HandshakeStateTransition,
                    $"Handshake: unsupported pending approval kind={item.Kind}",
                    peerId: _model.NetworkPeerId,
                    contextTag: "UnsupportedKind");
                break;
        }
    }

    private async Task ExecuteRejectPendingApprovalAsync(PendingApprovalItem item, CancellationToken ct)
    {
        if (item is null) return;

        switch (item.Kind)
        {
            case PendingHandshakeKind.InboundDirectInviteRequestFromMain:
                await _state.RejectPendingInboundDirectInviteAsync(
                    simulatedNetworkPeerId: _model.NetworkPeerId,
                    correlationId: item.CorrelationId,
                    cancellationToken: ct)
                    .ConfigureAwait(false);
                break;

            case PendingHandshakeKind.InboundStandardSignalHello:
                // Standard-signal hellos are runtime-only; rejection is implicit by clearing
                await InvokeOnUiAsync(() =>
                {
                    _model.PendingInboundStandardSignalHellosMutable.Remove(item.CorrelationId.ToString());
                }).ConfigureAwait(false);
                _diagnostics.Emit(
                    SimulatorDiagnosticEventType.HandshakeStateTransition,
                    $"Handshake: standard-signal hello rejected corr={item.CorrelationId}",
                    peerId: _model.NetworkPeerId,
                    contextTag: "StandardSignalRejected");
                break;

            default:
                _diagnostics.Emit(
                    SimulatorDiagnosticEventType.HandshakeStateTransition,
                    $"Handshake: unsupported pending approval kind={item.Kind}",
                    peerId: _model.NetworkPeerId,
                    contextTag: "UnsupportedKind");
                break;
        }
    }

    private void ExecuteForceExpire()
    {
        _ = InvokeOnUiAsync(() => _model.MarkExpired());
        _diagnostics.Emit(
            SimulatorDiagnosticEventType.HandshakeStateTransition,
            "Handshake: expired",
            peerId: _model.NetworkPeerId,
            contextTag: "Expired");
    }

    private void ExecuteReset()
    {
        _ = InvokeOnUiAsync(() => _model.ClearRuntimeState());
        _diagnostics.Emit(
            SimulatorDiagnosticEventType.HandshakeStateTransition,
            "Handshake: reset (no handshake)",
            peerId: _model.NetworkPeerId,
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
        var port = _model.Endpoint.CurrentValue.Port;
        if (port == 0) port = 5002;

        var inviterHost = _model.Endpoint.CurrentValue.Host;

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
