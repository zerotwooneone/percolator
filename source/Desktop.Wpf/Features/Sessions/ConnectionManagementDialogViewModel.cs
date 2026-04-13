using Desktop.Wpf.Shared.Mvvm;
using Google.Protobuf;
using R3;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Net;
using System.Security.Cryptography;
using System.Windows;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Network.Handshake;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using Desktop.Wpf.Features.Sessions.State;
using Grpc.Core;
using Percolator.Application.Services;
using MediatR;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PendingInvitationItem
{
    public required Guid PendingSessionId { get; init; }
    public required string DisplayName { get; init; }
    public required string Initials { get; init; }
    public required bool IsRelayed { get; init; }
    public string? RelayInfoText { get; init; }

    public string StatusText { get; set; } = "Pending";
    public string? SendPath { get; set; }
    public string? RequestCorrelationId { get; set; }
    public bool IsExpired { get; set; }
}

public sealed class TransportRouteOption
{
    public TransportRouteOption(Guid? relayHostPeerId, string displayName)
    {
        RelayHostPeerId = relayHostPeerId;
        DisplayName = displayName;
    }

    // null => Direct P2P (local mesh)
    public Guid? RelayHostPeerId { get; }
    public string DisplayName { get; }
}

public sealed class RouteModeOption
{
    public RouteModeOption(string key, string displayName)
    {
        Key = key;
        DisplayName = displayName;
    }

    public string Key { get; }
    public string DisplayName { get; }
}

public sealed class RelayHostOption
{
    public RelayHostOption(Guid peerId, string displayName)
    {
        PeerId = peerId;
        DisplayName = displayName;
    }

    public Guid PeerId { get; }
    public string DisplayName { get; }
}

public sealed class ConnectionManagementDialogViewModel : ViewModelBase
{
    private readonly DisposableBag _bag;

    private readonly IMainInvitationInbox _inbox;
    private readonly IMainInvitationActions _actions;
    private readonly IMainInvitationInboxEvents _inboxEvents;

    private readonly IMainReverseSignalInviteFactory _reverseSignalInvites;
    private readonly IGrpcSessionService _grpcSessions;
    private readonly IMessageTransportService _transport;
    private readonly ISecureMessagingService _secureMessaging;
    private readonly IDirectSessionRepository _directSessions;
    private readonly ActiveIdentityContext _active;
    private readonly IPeerIdentityRepository _peerIdentities;
    private readonly IEstablishDirectSessionService _establishDirectSession;
    private readonly ISecureChannelsStore _store;

    private readonly ISessionCrypto _sessionCrypto;
    private readonly IPreHandshakeSessionStore _preHandshake;
    private readonly ISentInvitationRepository _sentInvitations;
    private readonly IClock _clock;
    private readonly IMediator _mediator;

    private readonly object _relayHostRefreshLock = new();
    private bool _relayHostRefreshQueued;

    private readonly ObservableCollection<PendingInvitationItem> _pendingInvitations = new();
    private readonly ObservableCollection<RouteModeOption> _routeModeOptions = new();
    private readonly ObservableCollection<RelayHostOption> _relayHostOptions = new();

    public BindableReactiveProperty<int> SelectedTabIndex { get; }

    public BindableReactiveProperty<string?> TargetDisplayNameText { get; }

    public ReadOnlyObservableCollection<RouteModeOption> RouteModeOptions { get; }
    public BindableReactiveProperty<RouteModeOption?> SelectedRouteMode { get; }

    public BindableReactiveProperty<string?> DirectEndpointText { get; }

    public BindableReactiveProperty<string?> TargetPkhText { get; }

    public ReadOnlyObservableCollection<RelayHostOption> RelayHostOptions { get; }
    public BindableReactiveProperty<RelayHostOption?> SelectedRelayHost { get; }

    public BindableReactiveProperty<string?> PhaseText { get; }
    public BindableReactiveProperty<string?> ErrorText { get; }

    public BindableReactiveProperty<string?> InviteTokenText { get; }

    public ReadOnlyObservableCollection<PendingInvitationItem> PendingInvitations { get; }

    public AsyncRelayCommand AcceptInvitationCommand { get; }
    public AsyncRelayCommand BurnInvitationCommand { get; }
    public AsyncRelayCommand RefreshInboxCommand { get; }

    public AsyncRelayCommand SearchAndConnectCommand { get; }

    public AsyncRelayCommand DecodeAndInitiateCommand { get; }

    public ConnectionManagementDialogViewModel(
        IMainInvitationInbox inbox,
        IMainInvitationActions actions,
        IMainInvitationInboxEvents inboxEvents,
        IMainReverseSignalInviteFactory reverseSignalInvites,
        IGrpcSessionService grpcSessions,
        IMessageTransportService transport,
        ISecureMessagingService secureMessaging,
        IDirectSessionRepository directSessions,
        ActiveIdentityContext active,
        IPeerIdentityRepository peerIdentities,
        IEstablishDirectSessionService establishDirectSession,
        ISecureChannelsStore store,
        ISessionCrypto sessionCrypto,
        IPreHandshakeSessionStore preHandshake,
        ISentInvitationRepository sentInvitations,
        IClock clock,
        IMediator mediator)
    {
        _inbox = inbox;
        _actions = actions;
        _inboxEvents = inboxEvents;
        _reverseSignalInvites = reverseSignalInvites;
        _grpcSessions = grpcSessions;
        _transport = transport;
        _secureMessaging = secureMessaging;
        _directSessions = directSessions;
        _active = active;
        _peerIdentities = peerIdentities;
        _establishDirectSession = establishDirectSession;
        _store = store;

        _sessionCrypto = sessionCrypto;
        _preHandshake = preHandshake;
        _sentInvitations = sentInvitations;
        _clock = clock;
        _mediator = mediator;

        ((INotifyCollectionChanged)_store.Channels).CollectionChanged += OnChannelsChanged;
        SelectedTabIndex = new BindableReactiveProperty<int>(0).AddTo(ref _bag);

        PendingInvitations = new ReadOnlyObservableCollection<PendingInvitationItem>(_pendingInvitations);

        TargetDisplayNameText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        RouteModeOptions = new ReadOnlyObservableCollection<RouteModeOption>(_routeModeOptions);
        SelectedRouteMode = new BindableReactiveProperty<RouteModeOption?>(null).AddTo(ref _bag);

        DirectEndpointText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        TargetPkhText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        RelayHostOptions = new ReadOnlyObservableCollection<RelayHostOption>(_relayHostOptions);
        SelectedRelayHost = new BindableReactiveProperty<RelayHostOption?>(null).AddTo(ref _bag);

        PhaseText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);
        ErrorText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        InviteTokenText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        RefreshInboxCommand = new AsyncRelayCommand(async _ => await RefreshInboxAsync());
        AcceptInvitationCommand = new AsyncRelayCommand(async obj => await ExecuteAcceptAsync(obj));
        BurnInvitationCommand = new AsyncRelayCommand(async obj => await ExecuteBurnAsync(obj));

        SearchAndConnectCommand = new AsyncRelayCommand(async _ => await ExecuteNetworkSearchAsync());

        DecodeAndInitiateCommand = new AsyncRelayCommand(async _ => await ExecuteImportTokenAsync());

        _inboxEvents.Changed
            .SubscribeAwait(async (_, ct) => await RefreshInboxAsync(ct).ConfigureAwait(false), AwaitOperation.Drop)
            .AddTo(ref _bag);

        _ = InitializeAsync().ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void OnChannelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Relay host options are derived from DirectSessions, which may change after a handshake completes.
        // Refresh while the dialog is open so the Relay dropdown stays up-to-date.
        QueueRelayHostRefresh();
    }

    private void QueueRelayHostRefresh()
    {
        lock (_relayHostRefreshLock)
        {
            if (_relayHostRefreshQueued) return;
            _relayHostRefreshQueued = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshRelayHostOptionsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                lock (_relayHostRefreshLock)
                {
                    _relayHostRefreshQueued = false;
                }
            }
        });
    }

    private async Task ExecuteImportTokenAsync(CancellationToken ct = default)
    {
        ResetStatus();
        PhaseText.Value = "Decoding Token...";

        if (_active.Identity is null)
        {
            ErrorText.Value = "Identity not loaded.";
            PhaseText.Value = null;
            return;
        }

        EstablishDirectSessionRequest env;
        InviteHandshakeRequestPayload payload;
        try
        {
            var bytes = DecodeTokenToBytes(InviteTokenText.Value);
            env = EstablishDirectSessionRequest.Parser.ParseFrom(bytes);
            payload = InviteHandshakeRequestPayload.Parser.ParseFrom(env.Payload);
        }
        catch
        {
            ErrorText.Value = "Invalid token.";
            PhaseText.Value = null;
            return;
        }

        if (!env.HasInviterIdentityKey || env.InviterIdentityKey.Length == 0)
        {
            ErrorText.Value = "Token missing inviter identity key.";
            PhaseText.Value = null;
            return;
        }

        if (!env.HasPayload || env.Payload.Length == 0)
        {
            ErrorText.Value = "Token missing payload.";
            PhaseText.Value = null;
            return;
        }

        if (!env.HasPayloadSignature || env.PayloadSignature.Length == 0)
        {
            ErrorText.Value = "Token missing signature.";
            PhaseText.Value = null;
            return;
        }

        PhaseText.Value = "Verifying Signature...";

        if (!VerifyInvitePayloadSignature(
                inviterIdentityKeySpki: env.InviterIdentityKey.ToByteArray(),
                payloadBytes: env.Payload.ToByteArray(),
                signatureBytes: env.PayloadSignature.ToByteArray()))
        {
            ErrorText.Value = "Token signature invalid.";
            PhaseText.Value = null;
            return;
        }

        PhaseText.Value = "Queuing Invitation...";

        try
        {
            _ = await _establishDirectSession.QueueInviteAsync(
                    selfIdentityId: new Percolator.Identity.SelfId(_active.Identity.SelfIdentityId.Value),
                    inviterIdentityKeySpki: env.InviterIdentityKey.ToByteArray(),
                    payloadBytes: env.Payload.ToByteArray(),
                    payloadSignatureBytes: env.PayloadSignature.ToByteArray(),
                    isRelayed: false,
                    relayHostPeerId: null,
                    cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorText.Value = ex.Message;
            PhaseText.Value = null;
            return;
        }

        PhaseText.Value = null;

        await RefreshInboxAsync(ct).ConfigureAwait(false);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            SelectedTabIndex.Value = 0;
        }
        else
        {
            await dispatcher.InvokeAsync(() => SelectedTabIndex.Value = 0);
        }
    }

    private static bool VerifyInvitePayloadSignature(byte[] inviterIdentityKeySpki, byte[] payloadBytes, byte[] signatureBytes)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(inviterIdentityKeySpki, out _);
            return ecdsa.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] DecodeTokenToBytes(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Token required.");
        }

        var t = new string(token.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return Convert.FromBase64String(t);
    }

    private async Task InitializeAsync(CancellationToken ct = default)
    {
        await InvokeOnUiAsync(InitializeRouteModeOptions).ConfigureAwait(false);
        await RefreshRelayHostOptionsAsync(ct).ConfigureAwait(false);

        await RefreshInboxAsync(ct).ConfigureAwait(false);

        var dispatcher = Application.Current?.Dispatcher;
        var desired = PendingInvitations.Count > 0 ? 0 : 1;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            SelectedTabIndex.Value = desired;
        }
        else
        {
            await dispatcher.InvokeAsync(() => SelectedTabIndex.Value = desired);
        }
    }

    private void InitializeRouteModeOptions()
    {
        _routeModeOptions.Clear();
        _routeModeOptions.Add(new RouteModeOption("direct", "Direct"));
        _routeModeOptions.Add(new RouteModeOption("relay", "Via Relay Host"));
        SelectedRouteMode.Value ??= _routeModeOptions.FirstOrDefault();
    }

    private async Task RefreshRelayHostOptionsAsync(CancellationToken ct)
    {
        if (_active.Identity is null)
        {
            await InvokeOnUiAsync(() => _relayHostOptions.Clear()).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<DirectSession> sessions;
        try
        {
            sessions = await _directSessions.ListAsync(_active.Identity.SelfIdentityId.Value).ConfigureAwait(false);
        }
        catch
        {
            sessions = Array.Empty<DirectSession>();
        }

        var options = new List<RelayHostOption>();
        foreach (var s in sessions.OrderBy(x => x.RemotePeerId.Value))
        {
            var peerId = new Percolator.Identity.PeerId(s.RemotePeerId.Value);

            PeerIdentity? identity;
            try
            {
                identity = await _peerIdentities.GetByIdAsync(peerId, ct).ConfigureAwait(false);
            }
            catch
            {
                identity = null;
            }

            var name = identity?.DisplayName?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = peerId.Value.ToString()[..8];
            }

            options.Add(new RelayHostOption(peerId.Value, name));
        }

        await InvokeOnUiAsync(() =>
        {
            _relayHostOptions.Clear();
            foreach (var o in options)
                _relayHostOptions.Add(o);
            SelectedRelayHost.Value ??= _relayHostOptions.FirstOrDefault();
        }).ConfigureAwait(false);
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

    public async Task RefreshInboxAsync(CancellationToken ct = default)
    {
        IReadOnlyList<PendingInvitationDto> open;
        try
        {
            open = await _inbox.GetOpenAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        var items = open
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(p => new PendingInvitationItem
            {
                PendingSessionId = p.PendingSessionId,
                DisplayName = p.PeerName,
                Initials = ComputeInitials(p.PeerName),
                IsRelayed = p.IsRelayed,
                RelayInfoText = p.IsRelayed
                    ? $"Via relay: {p.RelayPeerName}{(string.IsNullOrWhiteSpace(p.RelayEndpoint) ? "" : $" ({p.RelayEndpoint})")}" 
                    : null
            })
            .ToList();

        void apply()
        {
            _pendingInvitations.Clear();
            foreach (var it in items)
                _pendingInvitations.Add(it);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            apply();
        }
        else
        {
            await dispatcher.InvokeAsync(apply);
        }
    }

    private async Task ExecuteAcceptAsync(object? obj)
    {
        if (obj is not PendingInvitationItem item) return;

        ApproveInvitationResult result;
        try
        {
            result = await _actions.ApproveAsync(item.PendingSessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            item.StatusText = $"Failed: {ex.Message}";
            return;
        }

        switch (result)
        {
            case ApproveInvitationResult.Accepted accepted:
                item.StatusText = "Accepted";
                item.SendPath = accepted.SendPath;
                item.RequestCorrelationId = accepted.RequestCorrelationId.ToString();
                item.IsExpired = false;
                await RefreshInboxAsync().ConfigureAwait(false);
                break;
            case ApproveInvitationResult.RejectedNotReady:
                item.StatusText = "Rejected: Not Ready";
                break;
            case ApproveInvitationResult.RejectedInvalid:
                item.StatusText = "Rejected: Invalid";
                break;
            case ApproveInvitationResult.RejectedExpired:
                item.StatusText = "Rejected: Expired";
                item.IsExpired = true;
                await RefreshInboxAsync().ConfigureAwait(false);
                break;
            case ApproveInvitationResult.Failed failed:
                item.StatusText = $"Failed: {failed.ErrorMessage}";
                break;
            default:
                item.StatusText = "Failed: Unknown";
                break;
        }
    }

    private async Task ExecuteBurnAsync(object? obj)
    {
        if (obj is not PendingInvitationItem item) return;

        try
        {
            await _actions.BurnAsync(item.PendingSessionId).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        await RefreshInboxAsync().ConfigureAwait(false);
    }

    private async Task ExecuteNetworkSearchAsync(CancellationToken ct = default)
    {
        ResetStatus();
        PhaseText.Value = "Starting...";

        if (_active.Identity is null)
        {
            ErrorText.Value = "Identity not loaded.";
            PhaseText.Value = null;
            return;
        }

        PhaseText.Value = "Validating...";

        try
        {
            var routeMode = SelectedRouteMode.Value;
            if (routeMode is null)
            {
                ErrorText.Value = "Select a route mode.";
                PhaseText.Value = null;
                return;
            }

            // Relay host options are derived from persisted direct sessions. Refresh on demand so the dropdown
            // reflects newly-established sessions even if no store-level collection change is emitted.
            await RefreshRelayHostOptionsAsync(ct);

            if (routeMode.Key == "direct")
            {
                var endpoint = ParseDnsEndPoint(DirectEndpointText.Value);

                PhaseText.Value = "Sending invite...";

                try
                {
                    var invite = _reverseSignalInvites.CreateInvite(
                        targetDisplayName: TargetDisplayNameText.Value,
                        targetEndpointHost: endpoint.Host,
                        targetEndpointPort: endpoint.Port);
                    _ = await _grpcSessions.EstablishDirectSessionAsync(endpoint, invite);
                    PhaseText.Value = "Invite sent.";
                }
                catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.Unavailable)
                {
                    ErrorText.Value = "Target offline.";
                    PhaseText.Value = null;
                }
                catch (Exception ex)
                {
                    ErrorText.Value = ex.Message;
                    PhaseText.Value = null;
                }

                return;
            }

            if (routeMode.Key == "relay")
            {
                if (SelectedRelayHost.Value is null)
                {
                    ErrorText.Value = "Select a relay host.";
                    PhaseText.Value = null;
                    return;
                }

                byte[] targetPkh;
                try
                {
                    targetPkh = ParsePkh32(TargetPkhText.Value);
                }
                catch (Exception ex)
                {
                    ErrorText.Value = ex.Message;
                    PhaseText.Value = null;
                    return;
                }

                if (_active.Identity is null)
                {
                    ErrorText.Value = "Identity not loaded.";
                    PhaseText.Value = null;
                    return;
                }

                var relayHostPeerId = new Percolator.Identity.PeerId(SelectedRelayHost.Value.PeerId);
                var selfIdentityId = _active.Identity.SelfIdentityId.Value;

                DirectSession? direct;
                try
                {
                    direct = await _directSessions
                        .GetByRemotePeerIdAsync(new Percolator.Network.PeerId(relayHostPeerId.Value), selfIdentityId)
                        ;
                }
                catch (Exception ex)
                {
                    ErrorText.Value = ex.Message;
                    PhaseText.Value = null;
                    return;
                }

                if (direct is null)
                {
                    ErrorText.Value = "No direct session to relay host.";
                    PhaseText.Value = null;
                    return;
                }

                PhaseText.Value = "Fetching pre-key bundle...";

                GetPreKeyBundleResponse.Types.PreKeyBundle? bundle;
                try
                {
                    var internalEnvelope = new InternalEnvelope
                    {
                        PrekeyEnvelope = new PrekeyEnvelope
                        {
                            Version = 1,
                            GetPreKeyBundleRequest = new GetPreKeyBundleRequest
                            {
                                Version = 1,
                                PublicKeyHash = ByteString.CopyFrom(targetPkh)
                            }
                        }
                    };

                    var plaintext = new Plaintext(internalEnvelope.ToByteArray());
                    var cryptoSessionId = new Percolator.Cryptography.SessionId(direct.SessionId.Value);
                    var cipher = await _secureMessaging
                        .EncryptAsync(cryptoSessionId, plaintext)
                        ;

                    var deliverResp = await _transport
                        .SendMessageAsync(relayHostPeerId, direct.SessionId, cipher)
                        ;

                    if (deliverResp.ResultCase != DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload
                        || deliverResp.ResponsePayload is null
                        || !deliverResp.ResponsePayload.HasResponsePayload
                        || deliverResp.ResponsePayload.ResponsePayload.Length == 0)
                    {
                        throw new InvalidOperationException("No response payload returned.");
                    }

                    var respCipher = new SessionRatchetMessage(deliverResp.ResponsePayload.ResponsePayload.ToByteArray());
                    var resolved = await _secureMessaging
                        .DecryptInboundAsync(selfIdentityId, respCipher)
                        ;
                    var respPlain = resolved?.plaintext;
                    if (respPlain is null)
                    {
                        throw new InvalidOperationException("Could not decrypt pre-key bundle response.");
                    }

                    var internalResp = InternalEnvelope.Parser.ParseFrom(respPlain.Value);
                    if (internalResp.ApplicationPayloadCase != InternalEnvelope.ApplicationPayloadOneofCase.GetPreKeyBundleResponse)
                    {
                        throw new InvalidOperationException("Unexpected response type.");
                    }

                    bundle = internalResp.GetPreKeyBundleResponse?.PreKeyBundle;
                }
                catch (Exception ex)
                {
                    ErrorText.Value = ex.Message;
                    PhaseText.Value = null;
                    return;
                }

                if (bundle is null)
                {
                    ErrorText.Value = "Target not found.";
                    PhaseText.Value = null;
                    return;
                }

                if (!bundle.HasIdentityKey || bundle.IdentityKey.Length == 0
                    || !bundle.HasSignedPreKeyId || bundle.SignedPreKeyId.Length == 0
                    || !bundle.HasSignedPreKey || bundle.SignedPreKey.Length == 0
                    || !bundle.HasPreKeySignature || bundle.PreKeySignature.Length == 0)
                {
                    ErrorText.Value = "Pre-key bundle invalid.";
                    PhaseText.Value = null;
                    return;
                }

                // Verify PKH matches returned identity key and verify signature.
                byte[] remoteIdentitySpki = bundle.IdentityKey.ToByteArray();
                byte[] actualRemotePkh;
                using (var sha = SHA256.Create())
                {
                    actualRemotePkh = sha.ComputeHash(remoteIdentitySpki);
                }
                if (!actualRemotePkh.AsSpan().SequenceEqual(targetPkh))
                {
                    ErrorText.Value = "Remote identity key does not match requested PKH.";
                    PhaseText.Value = null;
                    return;
                }

                Guid signedPreKeyId;
                try
                {
                    signedPreKeyId = new Guid(bundle.SignedPreKeyId.ToByteArray());
                }
                catch
                {
                    ErrorText.Value = "Signed pre-key id invalid.";
                    PhaseText.Value = null;
                    return;
                }

                Guid? oneTimePreKeyId = null;
                OneTimeKey? oneTimePreKey = null;
                if (bundle.OneTimeKeys.Count > 0)
                {
                    var first = bundle.OneTimeKeys.FirstOrDefault(k => k is not null && k.OneTimeKeyId.Length > 0 && k.KeyBytes.Length > 0);
                    if (first is not null)
                    {
                        try
                        {
                            oneTimePreKeyId = new Guid(first.OneTimeKeyId.ToByteArray());
                            oneTimePreKey = new OneTimeKey(first.KeyBytes.ToByteArray());
                        }
                        catch
                        {
                            oneTimePreKeyId = null;
                            oneTimePreKey = null;
                        }
                    }
                }

                var remoteIdentity = new RatchetIdentityKey(remoteIdentitySpki);
                var remoteSpk = new PreKey(bundle.SignedPreKey.ToByteArray());
                var remoteSig = new Percolator.Cryptography.Signature(bundle.PreKeySignature.ToByteArray());
                if (!_sessionCrypto.VerifySignature(remoteIdentity, remoteSpk, remoteSig))
                {
                    ErrorText.Value = "Pre-key bundle signature invalid.";
                    PhaseText.Value = null;
                    return;
                }

                if (_active.Keys?.IdentitySigningKey is null)
                {
                    ErrorText.Value = "Identity keys not loaded.";
                    PhaseText.Value = null;
                    return;
                }

                PhaseText.Value = "Preparing handshake...";

                // Build X3DH initiator state (persist only what is needed for slow-path finalize: initial root key).
                var pkb = new Percolator.Cryptography.PreKeyBundle(
                    remoteIdentity,
                    signedPreKeyId,
                    remoteSpk,
                    remoteSig,
                    oneTimePreKeyId,
                    oneTimePreKey,
                    expirationDateUtc: null);

                var localIkPriv = new PrivatePreKey(_active.Keys.IdentitySigningKey.ExportECPrivateKey());
                var x3 = _sessionCrypto.X3DH_Initiate(localIkPriv, pkb);

                var correlationId = Guid.NewGuid();
                var nowUtc = _clock.UtcNow;
                var expiresAtUtc = nowUtc.AddMinutes(10);

                try
                {
                    await _preHandshake.SaveAsync(
                            new PreHandshakeRecord(
                                Id: 0,
                                SelfIdentityId: selfIdentityId,
                                RecipientPublicKeyHash: targetPkh,
                                LocalRequestId: correlationId,
                                // Ephemeral private is no longer persisted; provide empty.
                                InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
                                InitialRootKey: x3.SharedSecret.Value,
                                CreatedAtUtc: nowUtc,
                                ExpiresAtUtc: expiresAtUtc,
                                RemoteIdentityKeySpki: remoteIdentitySpki),
                            CancellationToken.None)
                        ;
                }
                catch (Exception ex)
                {
                    ErrorText.Value = ex.Message;
                    PhaseText.Value = null;
                    return;
                }

                // Persist route provenance for PendingOutbound row (relay host), keyed by correlation id.
                try
                {
                    await _sentInvitations.UpsertAsync(
                            new SentInvitation(
                                new Percolator.Cryptography.Primitives.RequestCorrelationId(correlationId),
                                signedPreKeyId,
                                oneTimePreKeyId,
                                targetPeerId: null,
                                createdAtUtc: nowUtc,
                                expiresAtUtc: expiresAtUtc,
                                targetDisplayName: TargetDisplayNameText.Value,
                                targetEndpointHost: null,
                                targetEndpointPort: null,
                                inviteRouteKind: InviteRouteKind.Relayed,
                                inviteRelayHostPeerId: new Percolator.Cryptography.Primitives.PeerId(relayHostPeerId.Value)))
                        .ConfigureAwait(false);

                    await _mediator.Publish(
                            new SentInvitationUpsertedNotification(new Percolator.Cryptography.Primitives.RequestCorrelationId(correlationId)),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ErrorText.Value = ex.Message;
                    PhaseText.Value = null;
                    return;
                }

                // Create initiator hello and enqueue it to relay host for forwarding to target PKH.
                var hello = new HandshakeInitiatorHello
                {
                    Version = 1,
                    InitiatorIdentityKeySpki = ByteString.CopyFrom(_active.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
                    InitiatorEphemeralKeySpki = ByteString.CopyFrom(x3.EphemeralPublic.Value),
                    SignedPreKeyId = ByteString.CopyFrom(signedPreKeyId.ToByteArray())
                };
                if (oneTimePreKeyId is not null)
                {
                    hello.OneTimePreKeyId = ByteString.CopyFrom(oneTimePreKeyId.Value.ToByteArray());
                }

                PhaseText.Value = "Enqueuing handshake...";
                try
                {
                    var mqReq = new EnqueueOpaqueMessageRequest
                    {
                        Version = 1,
                        RecipientPublicKeyHash = ByteString.CopyFrom(targetPkh),
                        MessageBlob = ByteString.CopyFrom(hello.ToByteArray())
                    };

                    var env = new InternalEnvelope
                    {
                        MessageQueueEnvelope = new MessageQueueEnvelope
                        {
                            Version = 1,
                            EnqueueOpaqueMessageRequest = mqReq
                        }
                    };

                    var plainMq = new Plaintext(env.ToByteArray());
                    var cryptoSessionIdMq = new Percolator.Cryptography.SessionId(direct.SessionId.Value);
                    var cipherMq = await _secureMessaging
                        .EncryptAsync(cryptoSessionIdMq, plainMq)
                        .ConfigureAwait(false);

                    _ = await _transport
                        .SendMessageAsync(relayHostPeerId, direct.SessionId, cipherMq, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ErrorText.Value = ex.Message;
                    PhaseText.Value = null;
                    return;
                }

                PhaseText.Value = "Handshake enqueued.";
                return;
            }

            ErrorText.Value = "Unknown route mode.";
            PhaseText.Value = null;
        }
        catch (Exception ex)
        {
            ErrorText.Value = ex.Message;
            PhaseText.Value = null;
        }
    }

    private void ResetStatus()
    {
        PhaseText.Value = null;
        ErrorText.Value = null;
    }

    private static DnsEndPoint ParseDnsEndPoint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Endpoint required.");
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring("http://".Length);
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring("https://".Length);

        var parts = trimmed.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port) || port <= 0)
        {
            throw new InvalidOperationException("Invalid endpoint format. Use host:port");
        }

        return new DnsEndPoint(parts[0], port);
    }

    private static byte[] ParsePkh32(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Target PKH required.");

        var t = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            t = t.Substring(2);

        if (t.Length % 2 != 0)
            throw new InvalidOperationException("PKH must be hex.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(t);
        }
        catch
        {
            throw new InvalidOperationException("PKH must be hex.");
        }

        if (bytes.Length != 32)
            throw new InvalidOperationException("PKH must be 32 bytes (64 hex chars).");

        return bytes;
    }

    private static string ComputeInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
            return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return (parts[0][0].ToString() + parts[^1][0].ToString()).ToUpperInvariant();
    }

    protected override void DisposeCore()
    {
        Disposable.Dispose(SelectedTabIndex);
        Disposable.Dispose(TargetDisplayNameText);
        Disposable.Dispose(SelectedRouteMode);
        Disposable.Dispose(DirectEndpointText);
        Disposable.Dispose(TargetPkhText);
        Disposable.Dispose(SelectedRelayHost);
        Disposable.Dispose(PhaseText);
        Disposable.Dispose(ErrorText);
        Disposable.Dispose(InviteTokenText);
        ((INotifyCollectionChanged)_store.Channels).CollectionChanged -= OnChannelsChanged;
        _bag.Dispose();
    }
}
