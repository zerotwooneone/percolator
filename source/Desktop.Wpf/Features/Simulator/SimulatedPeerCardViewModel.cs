using System.Linq;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Windows;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using R3;
using Percolator.Contracts;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatedPeerCardViewModel : IDisposable
{
    private readonly SimulatedPeerModel _model;
    private readonly ISimulatorStateService _state;
    private readonly ISimulatedPeerRuntimeService _runtime;
    private readonly Func<Guid, string> _resolvePeerName;
    private readonly Action _relationshipsChanged;
    private DisposableBag _bag;

    public SimulatedPeerCardViewModel(
        SimulatedPeerModel model,
        ISimulatorStateService state,
        ISimulatedPeerRuntimeService runtime,
        Func<Guid, string> resolvePeerName,
        Action relationshipsChanged)
    {
        _model = model;
        _state = state;
        _runtime = runtime;
        _resolvePeerName = resolvePeerName;
        _relationshipsChanged = relationshipsChanged;

        DisplayName = _model.DisplayName
            .Select(n => string.IsNullOrWhiteSpace(n) ? _model.PeerId.ToString()[..8] : n!)
            .ToBindableReactiveProperty(_model.PeerId.ToString()[..8])
            .AddTo(ref _bag);

        IsOnline = _model.IsOnline
            .ToBindableReactiveProperty(_model.IsOnline.CurrentValue)
            .AddTo(ref _bag);

        IsOnline
            .DistinctUntilChanged()
            .Subscribe(isOnline => _model.SetOnline(isOnline))
            .AddTo(ref _bag);

        IsRelayCapable = _model.IsRelayCapable
            .ToBindableReactiveProperty(_model.IsRelayCapable.CurrentValue)
            .AddTo(ref _bag);

        IsRelayCapable
            .DistinctUntilChanged()
            .Subscribe(isRelay => _model.SetRelayCapable(isRelay))
            .AddTo(ref _bag);

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

        var toggleOnline = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        toggleOnline.AsObservable().Subscribe(_ => _model.SetOnline(!_model.IsOnline.CurrentValue)).AddTo(ref _bag);
        ToggleOnlineCommand = toggleOnline.AddTo(ref _bag);

        var togglePower = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        togglePower.AsObservable().Subscribe(_ => _model.SetOnline(!_model.IsOnline.CurrentValue)).AddTo(ref _bag);
        TogglePowerCommand = togglePower.AddTo(ref _bag);

        var toggleRelay = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        toggleRelay.AsObservable().Subscribe(_ => _model.SetRelayCapable(!_model.IsRelayCapable.CurrentValue)).AddTo(ref _bag);
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

        var publish = PublishTargetPeerId.Select(id => id is not null).ToReactiveCommand<Unit>(_ => { });
        publish.AsObservable().SubscribeAwait(async (_, ct) => await ExecutePublishAsync(ct), AwaitOperation.Drop).AddTo(ref _bag);
        PublishKeysCommand = publish.AddTo(ref _bag);

        PublishedToTags = new ObservableCollection<RelationshipTagViewModel>();
        HostingForTags = new ObservableCollection<RelationshipTagViewModel>();

        AvailablePublishTargets = new ObservableCollection<PublishTargetOption>();

        _ = InitializeAsync();
    }

    public Guid PeerId => _model.PeerId;

    public BindableReactiveProperty<string> DisplayName { get; }

    public BindableReactiveProperty<bool> IsOnline { get; }

    public BindableReactiveProperty<bool> IsRelayCapable { get; }

    public BindableReactiveProperty<string> PublicKeyHashHex { get; }

    public BindableReactiveProperty<string> PublicKeyHashShort { get; }

    public BindableReactiveProperty<string> PublicKeyHashDisplay { get; }

    public BindableReactiveProperty<string> EndpointText { get; }

    public BindableReactiveProperty<Visibility> RelayBadgeVisibility { get; }

    public BindableReactiveProperty<Guid?> PublishTargetPeerId { get; }

    public BindableReactiveProperty<bool> IncludeOneTimeKeys { get; }

    public BindableReactiveProperty<int> OneTimeKeyCount { get; }

    public ObservableCollection<RelationshipTagViewModel> PublishedToTags { get; }

    public ObservableCollection<RelationshipTagViewModel> HostingForTags { get; }

    public ObservableCollection<PublishTargetOption> AvailablePublishTargets { get; }

    public ReactiveCommand<Unit> ToggleOnlineCommand { get; }

    public ReactiveCommand<Unit> TogglePowerCommand { get; }

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
            var pkh = await _runtime.ComputePublicKeyHashAsync(_model.PeerId, CancellationToken.None).ConfigureAwait(false);
            var hex = Convert.ToHexString(pkh);
            await Application.Current.Dispatcher.InvokeAsync(() => PublicKeyHashHex.Value = hex);
        }
        catch
        {
            // ignore
        }

        try
        {
            var endpoint = TryResolveEndpoint();
            if (endpoint is not null)
            {
                await Application.Current.Dispatcher.InvokeAsync(() => EndpointText.Value = endpoint);
            }
        }
        catch
        {
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
        var endpoint = TryResolveEndpointParts();
        var inviterHost = endpoint.host;
        var inviterPort = endpoint.port;

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
        _runtime.RecordOutboundInviteSignedPreKeyPrivate(_model.PeerId, correlation, inviterSignedPreKeyPriv);
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
        var dto = _state.Peers.FirstOrDefault(p => p.PeerId == _model.PeerId);
        if (dto is null) return null;
        if (string.IsNullOrWhiteSpace(dto.Connection?.Host) || dto.Connection.Port <= 0) return null;
        return $"{dto.Connection.Host}:{dto.Connection.Port}";
    }

    private (string host, int port) TryResolveEndpointParts()
    {
        var dto = _state.Peers.FirstOrDefault(p => p.PeerId == _model.PeerId);
        if (dto is null
            || string.IsNullOrWhiteSpace(dto.Connection?.Host)
            || dto.Connection.Port <= 0)
        {
            return (AllocateSimulatorLoopbackHost(_model.PeerId), 5002);
        }

        return (dto.Connection.Host, dto.Connection.Port);
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

    private async Task ExecutePublishAsync(CancellationToken ct)
    {
        var hostPeerId = PublishTargetPeerId.Value;
        if (hostPeerId is null) return;

        await _state.AddPublishedKeysRelationshipAsync(_model.PeerId, hostPeerId.Value, ct).ConfigureAwait(false);

        // In our simulator, "publishing" means pushing a standard pre-key bundle into the host's pre-key store.
        await _runtime.PublishStandardPreKeyBundleToRelayAsync(
                simulatedPeerId: _model.PeerId,
                relayHostPeerId: hostPeerId.Value,
                expiresUtc: DateTimeOffset.UtcNow.AddHours(12),
                includeOneTimeKeys: IncludeOneTimeKeys.Value,
                oneTimeKeyCount: OneTimeKeyCount.Value,
                cancellationToken: ct)
            .ConfigureAwait(false);

        _relationshipsChanged();
    }

    internal void RebuildRelationshipTags(SimulatedPeerDto dto, ReadOnlyObservableCollection<SimulatedPeerDto> allPeers)
    {
        PublishedToTags.Clear();
        HostingForTags.Clear();

        AvailablePublishTargets.Clear();
        foreach (var p in allPeers.Where(p => p.PeerId != PeerId))
        {
            AvailablePublishTargets.Add(new PublishTargetOption(p.PeerId, _resolvePeerName(p.PeerId)));
        }

        dto.PublishedKeysToPeerIds ??= new();
        foreach (var hostId in dto.PublishedKeysToPeerIds.Distinct().Where(x => x != PeerId))
        {
            var display = _resolvePeerName(hostId);
            PublishedToTags.Add(new RelationshipTagViewModel(
                peerId: hostId,
                display: $"Published to: {display}",
                onRemove: async ct =>
                {
                    await _state.RemovePublishedKeysRelationshipAsync(PeerId, hostId, ct).ConfigureAwait(false);
                    _relationshipsChanged();
                }));
        }

        foreach (var publisher in allPeers)
        {
            if (publisher.PeerId == PeerId) continue;
            if (publisher.PublishedKeysToPeerIds?.Contains(PeerId) != true) continue;

            var display = _resolvePeerName(publisher.PeerId);
            HostingForTags.Add(new RelationshipTagViewModel(
                peerId: publisher.PeerId,
                display: $"Hosting keys for: {display}",
                onRemove: async ct =>
                {
                    await _state.RemovePublishedKeysRelationshipAsync(publisher.PeerId, PeerId, ct).ConfigureAwait(false);
                    _relationshipsChanged();
                }));
        }
    }

    public void Dispose()
    {
        _bag.Dispose();
        DisplayName.Dispose();
        IsOnline.Dispose();
        IsRelayCapable.Dispose();
        PublicKeyHashHex.Dispose();
        PublishTargetPeerId.Dispose();
        IncludeOneTimeKeys.Dispose();
        OneTimeKeyCount.Dispose();
    }

    public sealed class RelationshipTagViewModel
    {
        private readonly Func<CancellationToken, Task> _remove;

        public RelationshipTagViewModel(Guid peerId, string display, Func<CancellationToken, Task> onRemove)
        {
            PeerId = peerId;
            Display = display;
            _remove = onRemove;

            var cmd = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
            cmd.AsObservable().SubscribeAwait(async (_, ct) => await _remove(ct), AwaitOperation.Drop);
            RemoveCommand = cmd;
        }

        public Guid PeerId { get; }

        public string Display { get; }

        public ReactiveCommand<Unit> RemoveCommand { get; }
    }

    public sealed class PublishTargetOption
    {
        public PublishTargetOption(Guid peerId, string display)
        {
            PeerId = peerId;
            Display = display;
        }

        public Guid PeerId { get; }

        public string Display { get; }
    }
}
