using System.Security.Cryptography;
using System.Windows;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using ObservableCollections;
using R3;
using Percolator.Contracts;
using Percolator.Network;
using Desktop.Wpf.Features.Simulator.Models;
using Desktop.Wpf.Shared.Mvvm;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatedPeerCardViewModel : IDisposable
{
    private readonly SimulatedPeerModel _model;
    private readonly IUiDispatcher _ui;
    private readonly ISimulatorStateService _state;
    private readonly ISimulatorDiagnosticsService _diagnostics;
    private readonly Func<Guid, string> _resolvePeerName;
    private DisposableBag _bag;
    private bool _disposed;

    private readonly ISynchronizedView<SimulatedPeerModel, PublishTargetOption> _availableTargetsView;
    private readonly ISynchronizedView<PeerRelationship, RelationshipTagViewModel> _publishedToView;
    private readonly ISynchronizedView<PeerRelationship, RelationshipTagViewModel> _hostingForView;

    public SimulatedPeerCardViewModel(
        SimulatedPeerModel model,
        IUiDispatcher ui,
        ISimulatorStateService state,
        ISimulatorDiagnosticsService diagnostics,
        Func<Guid, string> resolvePeerName)
    {
        _model = model;
        _ui = ui;
        _state = state;
        _diagnostics = diagnostics;
        _resolvePeerName = resolvePeerName;

        DisplayName = _model.DisplayName
            .Select(n => string.IsNullOrWhiteSpace(n) ? _model.PeerId.ToString()[..8] : n!)
            .ToBindableReactiveProperty(_model.PeerId.ToString()[..8])
            .AddTo(ref _bag);

        IsRelayCapable = _model.IsRelayCapable
            .ToBindableReactiveProperty()
            .AddTo(ref _bag);
        
        _bag.Add(IsRelayCapable.Subscribe(newValue=> _model.IsRelayCapable.Value = newValue));

        PublicKeyHashHex = new BindableReactiveProperty<string>(string.Empty).AddTo(ref _bag);
        PublicKeyHashShort = PublicKeyHashHex
            .Select(x => string.IsNullOrWhiteSpace(x) ? string.Empty : (x.Length <= 16 ? x : x[..16] + "…"))
            .ToBindableReactiveProperty(string.Empty)
            .AddTo(ref _bag);

        PublicKeyHashDisplay = PublicKeyHashHex
            .Select(ToPkhDisplay)
            .ToBindableReactiveProperty(string.Empty)
            .AddTo(ref _bag);

        EndpointText = new BindableReactiveProperty<string>(string.Empty).AddTo(ref _bag);

        RelayBadgeVisibility = IsRelayCapable
            .Select(x => x ? Visibility.Visible : Visibility.Collapsed)
            .ToBindableReactiveProperty(Visibility.Collapsed)
            .AddTo(ref _bag);

        IncludeOneTimeKeys = new BindableReactiveProperty<bool>(true).AddTo(ref _bag);
        OneTimeKeyCount = new BindableReactiveProperty<int>(5).AddTo(ref _bag);

        PublishTargetPeerId = new BindableReactiveProperty<Guid?>(null).AddTo(ref _bag);

        var toggleRelay = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        toggleRelay.AsObservable()
            .Subscribe(_ =>
            {
                IsRelayCapable.Value = !IsRelayCapable.CurrentValue;
            })
            .AddTo(ref _bag);
        ToggleRelayCapableCommand = toggleRelay.AddTo(ref _bag);

        var copy = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        copy.AsObservable().Subscribe(_ => ExecuteCopyPkh()).AddTo(ref _bag);
        CopyPublicKeyHashCommand = copy.AddTo(ref _bag);

        var copyOob = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        copyOob.AsObservable().Subscribe(_ => ExecuteCopyOobInviteToken()).AddTo(ref _bag);
        CopyOobInviteTokenCommand = copyOob.AddTo(ref _bag);

        var copyEndpoint = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        copyEndpoint.AsObservable().Subscribe(_ => ExecuteCopyEndpoint()).AddTo(ref _bag);
        CopyEndpointCommand = copyEndpoint.AddTo(ref _bag);

        var publish = PublishTargetPeerId
            .Select(hostId =>
            {
                var host = hostId is null ? null : _state.Peers.FirstOrDefault(p => p.PeerId == new PeerId(hostId.Value));
                return host?.IsRelayCapable ?? Observable.Return(false);
            })
            .Switch()
            .ToReactiveCommand<Unit>(_ => { });
        publish.AsObservable().SubscribeAwait(async (_, ct) => await ExecutePublishAsync(ct), AwaitOperation.Drop).AddTo(ref _bag);
        PublishKeysCommand = publish.AddTo(ref _bag);

        // 1. Available Targets (Simplified: All peers except self)
        _availableTargetsView = _state.Peers
            .CreateView(p => new PublishTargetOption(p.PeerId, _resolvePeerName(p.PeerId.Value)))
            .AddTo(ref _bag);
        _availableTargetsView.AttachFilter((p, _) => p.PeerId != _model.PeerId);
        AvailablePublishTargets = _availableTargetsView.ToNotifyCollectionChanged(_ui.CollectionEventDispatcher).AddTo(ref _bag);

        // 2. Published To View (Projected from the flat Relationship Graph)
        _publishedToView = _state.Relationships
            .CreateView(rel => new RelationshipTagViewModel(
                peerId: rel.TargetPeerId,
                display: _resolvePeerName(rel.TargetPeerId.Value),
                onRemove: async ct => await _state.RemovePublishedKeysRelationshipAsync(_model.PeerId, rel.TargetPeerId, ct)))
            .AddTo(ref _bag);
        _publishedToView.AttachFilter((rel, _) => rel.SourcePeerId == _model.PeerId && rel.Type == RelationshipType.PublishedKey);
        _publishedToView.ObserveRemove().Subscribe(evt => evt.Value.View.Dispose()).AddTo(ref _bag);
        PublishedToTags = _publishedToView.ToNotifyCollectionChanged(_ui.CollectionEventDispatcher).AddTo(ref _bag);

        // 3. Hosting For View (Projected from the flat Relationship Graph)
        _hostingForView = _state.Relationships
            .CreateView(rel => new RelationshipTagViewModel(
                peerId: rel.SourcePeerId,
                display: _resolvePeerName(rel.SourcePeerId.Value),
                onRemove: async ct => await _state.RemovePublishedKeysRelationshipAsync(rel.SourcePeerId, _model.PeerId, ct)))
            .AddTo(ref _bag);
        _hostingForView.AttachFilter((rel, _) => rel.TargetPeerId == _model.PeerId && rel.Type == RelationshipType.PublishedKey);
        _hostingForView.ObserveRemove().Subscribe(evt => evt.Value.View.Dispose()).AddTo(ref _bag);
        HostingForTags = _hostingForView.ToNotifyCollectionChanged(_ui.CollectionEventDispatcher).AddTo(ref _bag);

        ChatViewModel = new SimulatorChatViewModel(_model, _state, _ui);

        _ = InitializeAsync();
    }

    public PeerId PeerId => _model.PeerId;

    public BindableReactiveProperty<string> DisplayName { get; }

    public BindableReactiveProperty<bool> IsRelayCapable { get; }

    public BindableReactiveProperty<string> PublicKeyHashHex { get; }

    public BindableReactiveProperty<string> PublicKeyHashShort { get; }

    public BindableReactiveProperty<string> PublicKeyHashDisplay { get; }

    public BindableReactiveProperty<string> EndpointText { get; }

    public BindableReactiveProperty<Visibility> RelayBadgeVisibility { get; }

    public BindableReactiveProperty<Guid?> PublishTargetPeerId { get; }

    public BindableReactiveProperty<bool> IncludeOneTimeKeys { get; }

    public BindableReactiveProperty<int> OneTimeKeyCount { get; }

    public NotifyCollectionChangedSynchronizedViewList<RelationshipTagViewModel> PublishedToTags { get; }

    public NotifyCollectionChangedSynchronizedViewList<RelationshipTagViewModel> HostingForTags { get; }

    public NotifyCollectionChangedSynchronizedViewList<PublishTargetOption> AvailablePublishTargets { get; }

    public SimulatorChatViewModel ChatViewModel { get; }

    public ReactiveCommand<Unit> ToggleRelayCapableCommand { get; }

    public ReactiveCommand<Unit> CopyPublicKeyHashCommand { get; }

    public ReactiveCommand<Unit> CopyOobInviteTokenCommand { get; }

    public ReactiveCommand<Unit> CopyEndpointCommand { get; }

    public ReactiveCommand<Unit> PublishKeysCommand { get; }

    private static string ToPkhDisplay(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return string.Empty;
        var x = hex.Trim();
        if (x.Length <= 10) return "0x" + x;
        var head = x[..4];
        var tail = x[^3..];
        return $"0x{head}...{tail}";
    }

    private async Task InitializeAsync()
    {
        try
        {
            var pkh = await _state.ComputePublicKeyHashAsync(_model.PeerId, CancellationToken.None).ConfigureAwait(false);
            var hex = Convert.ToHexString(pkh);
            if (_disposed) return;
            await _ui.InvokeAsync(() =>
            {
                if (_disposed) return;
                PublicKeyHashHex.Value = hex;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        try
        {
            var endpoint = _model.Endpoint.CurrentValue;
            var endpointStr = endpoint is null ? null : $"{endpoint.Host}:{endpoint.Port}";
            if (endpointStr is not null)
            {
                if (_disposed) return;
                await _ui.InvokeAsync(() =>
                {
                    if (_disposed) return;
                    EndpointText.Value = endpointStr;
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // ignore
        }
    }

    private void ExecuteCopyPkh()
    {
        var text = PublicKeyHashHex.Value;
        if (string.IsNullOrWhiteSpace(text)) return;
        try { Clipboard.SetText(text); } catch { }
    }

    private void ExecuteCopyOobInviteToken()
    {
        try
        {
            var invite = CreatePeerToMainInvite();
            var token = Convert.ToBase64String(invite.ToByteArray());
            if (string.IsNullOrWhiteSpace(token)) return;
            Clipboard.SetText(token);
        }
        catch
        {
        }
    }

    private void ExecuteCopyEndpoint()
    {
        var endpoint = TryResolveEndpoint();
        if (string.IsNullOrWhiteSpace(endpoint)) return;
        try { Clipboard.SetText(endpoint); } catch { }
    }

    private EstablishDirectSessionRequest CreatePeerToMainInvite()
    {
        // Peer inviter must advertise its simulator endpoint so the main app can route responses back in-process.
        var inviterHost = _model.Endpoint.CurrentValue.Host;
        var inviterPort = _model.Endpoint.CurrentValue.Port;

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
            InviterPort = (uint)inviterPort,
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

    private string? TryResolveEndpoint()
    {
        var endpoint = _model.Endpoint.CurrentValue;
        if (endpoint is null) return null;
        return $"{endpoint.Host}:{endpoint.Port}";
    }

    private async Task ExecutePublishAsync(CancellationToken ct)
    {
        var hostPeerId = PublishTargetPeerId.Value;
        if (hostPeerId is null) return;

        if (!HasActiveSessionToHost(new PeerId(hostPeerId.Value)))
        {
            _diagnostics.Emit(
                SimulatorDiagnosticEventType.PreKeyPublishBlockedMissingActiveSession,
                $"Pre-key publish blocked (missing active session): publisher={_model.PeerId.Value.ToString()[..8]} relay={hostPeerId.Value.ToString()[..8]}",
                peerId: _model.PeerId,
                relayHostPeerId: new PeerId(hostPeerId.Value));
            return;
        }

        await _state.AddPublishedKeysRelationshipAsync(_model.PeerId, new PeerId(hostPeerId.Value), ct).ConfigureAwait(false);

        // In our simulator, "publishing" means pushing a standard pre-key bundle into the host's pre-key store.
        await _state.PublishStandardPreKeyBundleToRelayAsync(
                simulatedPeerId: _model.PeerId,
                relayHostPeerId: new PeerId(hostPeerId.Value),
                expiresUtc: DateTimeOffset.UtcNow.AddHours(12),
                oneTimeKeyCount: OneTimeKeyCount.Value,
                cancellationToken: ct)
            .ConfigureAwait(false);
    }

    private bool HasActiveSessionToHost(PeerId relayHostPeerId)
    {
        return _state.Relationships.Contains(new PeerRelationship(relayHostPeerId, _model.PeerId, RelationshipType.RelayActiveSession));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disposable.Dispose(CopyOobInviteTokenCommand);
        Disposable.Dispose(CopyEndpointCommand);
        Disposable.Dispose(PublishKeysCommand);

        ChatViewModel.Dispose();

        _bag.Dispose();
        DisplayName.Dispose();
        IsRelayCapable.Dispose();
        PublicKeyHashHex.Dispose();
        PublishTargetPeerId.Dispose();
        IncludeOneTimeKeys.Dispose();
        OneTimeKeyCount.Dispose();
    }

    public sealed class RelationshipTagViewModel : IDisposable
    {
        private readonly Func<CancellationToken, Task> _remove;

        public RelationshipTagViewModel(PeerId peerId, string display, Func<CancellationToken, Task> onRemove)
        {
            PeerId = peerId;
            Display = display;
            _remove = onRemove;

            var cmd = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
            cmd.AsObservable().SubscribeAwait(async (_, ct) => await _remove(ct), AwaitOperation.Drop);
            RemoveCommand = cmd;
        }

        public PeerId PeerId { get; }

        public string Display { get; }

        public ReactiveCommand<Unit> RemoveCommand { get; }

        public void Dispose()
            => Disposable.Dispose(RemoveCommand);
    }

    public sealed class PublishTargetOption
    {
        public PublishTargetOption(PeerId peerId, string display)
        {
            PeerId = peerId;
            Display = display;
        }

        public PeerId PeerId { get; }

        public string Display { get; }
    }
}
