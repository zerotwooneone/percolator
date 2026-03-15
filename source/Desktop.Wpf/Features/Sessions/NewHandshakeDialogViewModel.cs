using System.Collections.ObjectModel;
using System.Net;
using System.Security.Cryptography;
using System.Windows;
using Desktop.Wpf.Shared.Mvvm;
using Grpc.Core;
using Google.Protobuf;
using System.Windows.Input;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using Percolator.Network.ValueObjects;
using R3;
using IdentityPeerId = Percolator.Identity.PeerId;
using NetworkPeerId = Percolator.Network.PeerId;

namespace Desktop.Wpf.Features.Sessions;

public sealed class RelayNodeOption
{
    public RelayNodeOption(IdentityPeerId? peerId, string displayText)
    {
        PeerId = peerId;
        DisplayText = displayText;
    }

    public IdentityPeerId? PeerId { get; }
    public string DisplayText { get; }
}

public sealed class NewHandshakeDialogViewModel : ViewModelBase
{
    private readonly DisposableBag _bag;
    private readonly ActiveIdentityContext _active;
    private readonly ISelfPreKeyBundleRepository _selfPreKeys;
    private readonly IOneTimeKeyProvider _oneTimeKeys;
    private readonly ISessionCrypto _sessionCrypto;
    private readonly IMainReverseSignalInviteFactory _reverseSignalInvites;
    private readonly IGrpcSessionService _grpcSessions;
    private readonly IMessageTransportService _transport;
    private readonly ISecureMessagingService _secureMessaging;
    private readonly ISessionRepository _sessions;
    private readonly IDirectSessionRepository _directSessions;
    private readonly IPeerRoutingProfileRepository _routingProfiles;
    private readonly IPeerIdentityRepository _peerIdentities;
    private readonly IPeerPublicSigningKeyStore _peerKeyStore;
    private readonly IClock _clock;
    private readonly TimeProvider _timeProvider;

    private readonly ObservableCollection<RelayNodeOption> _relayOptions = new();

    public NewHandshakeDialogViewModel(
        ActiveIdentityContext active,
        ISelfPreKeyBundleRepository selfPreKeys,
        IOneTimeKeyProvider oneTimeKeys,
        ISessionCrypto sessionCrypto,
        IMainReverseSignalInviteFactory reverseSignalInvites,
        IGrpcSessionService grpcSessions,
        IMessageTransportService transport,
        ISecureMessagingService secureMessaging,
        ISessionRepository sessions,
        IDirectSessionRepository directSessions,
        IPeerRoutingProfileRepository routingProfiles,
        IPeerIdentityRepository peerIdentities,
        IPeerPublicSigningKeyStore peerKeyStore,
        IClock clock,
        TimeProvider? timeProvider = null)
    {
        _active = active;
        _selfPreKeys = selfPreKeys;
        _oneTimeKeys = oneTimeKeys;
        _sessionCrypto = sessionCrypto;
        _reverseSignalInvites = reverseSignalInvites;
        _grpcSessions = grpcSessions;
        _transport = transport;
        _secureMessaging = secureMessaging;
        _sessions = sessions;
        _directSessions = directSessions;
        _routingProfiles = routingProfiles;
        _peerIdentities = peerIdentities;
        _peerKeyStore = peerKeyStore;
        _clock = clock;
        _timeProvider = timeProvider ?? ObservableSystem.DefaultTimeProvider;

        SelectedTabIndex = new BindableReactiveProperty<int>(0).AddTo(ref _bag);

        RelayOptions = new ReadOnlyObservableCollection<RelayNodeOption>(_relayOptions);
        SelectedRelay = new BindableReactiveProperty<RelayNodeOption?>(null).AddTo(ref _bag);
        RelayEndpointText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);
        TargetPkhText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        TargetEndpointText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);
        InviteTokenText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        GeneratedTokenText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);
        TokenValidityText = new BindableReactiveProperty<string>("VALID FOR 24H").AddTo(ref _bag);
        SelfPkhText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        PhaseText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);
        ErrorText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        SearchAndConnectCommand = new AsyncRelayCommand(_ => ExecuteNetworkSearchAsync());
        DecodeAndInitiateCommand = new AsyncRelayCommand(_ => ExecuteImportTokenAsync());

        var generateCommand = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        generateCommand.AsObservable()
            .Debounce(TimeSpan.FromMilliseconds(500), _timeProvider)
            .SubscribeAwait(async (_, ct) => await ExecuteGenerateInviteAsync().ConfigureAwait(false), AwaitOperation.Drop)
            .AddTo(ref _bag);
        GenerateNewTokenCommand = generateCommand.AddTo(ref _bag);

        CopyGeneratedTokenCommand = new AsyncRelayCommand(_ =>
        {
            var txt = GeneratedTokenText.Value;
            if (!string.IsNullOrWhiteSpace(txt))
            {
                Clipboard.SetText(txt);
            }
            return Task.CompletedTask;
        });

        CopySelfPkhCommand = new AsyncRelayCommand(_ =>
        {
            var txt = SelfPkhText.Value;
            if (!string.IsNullOrWhiteSpace(txt))
            {
                Clipboard.SetText(txt);
            }
            return Task.CompletedTask;
        });

        _ = InitializeAsync();
    }

    public BindableReactiveProperty<int> SelectedTabIndex { get; }

    public ReadOnlyObservableCollection<RelayNodeOption> RelayOptions { get; }
    public BindableReactiveProperty<RelayNodeOption?> SelectedRelay { get; }
    public BindableReactiveProperty<string?> RelayEndpointText { get; }
    public BindableReactiveProperty<string?> TargetPkhText { get; }

    public BindableReactiveProperty<string?> TargetEndpointText { get; }
    public BindableReactiveProperty<string?> InviteTokenText { get; }

    public BindableReactiveProperty<string?> GeneratedTokenText { get; }
    public BindableReactiveProperty<string> TokenValidityText { get; }
    public BindableReactiveProperty<string?> SelfPkhText { get; }

    public BindableReactiveProperty<string?> PhaseText { get; }
    public BindableReactiveProperty<string?> ErrorText { get; }

    public AsyncRelayCommand SearchAndConnectCommand { get; }
    public AsyncRelayCommand DecodeAndInitiateCommand { get; }
    public ICommand GenerateNewTokenCommand { get; }
    public AsyncRelayCommand CopyGeneratedTokenCommand { get; }
    public AsyncRelayCommand CopySelfPkhCommand { get; }

    private Task InitializeAsync()
    {
        if (_active.Keys?.IdentitySigningKey is not null)
        {
            var spki = _active.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo();
            var pkh = SHA256.HashData(spki);
            SelfPkhText.Value = ToShortHex(pkh);
        }

        _relayOptions.Clear();
        _relayOptions.Add(new RelayNodeOption(null, "Custom Relay..."));
        SelectedRelay.Value = _relayOptions.FirstOrDefault();

        return Task.CompletedTask;
    }

    private async Task ExecuteGenerateInviteAsync()
    {
        ResetStatus();

        if (_active.Identity is null || _active.Keys?.IdentitySigningKey is null)
        {
            ErrorText.Value = "Identity not loaded.";
            return;
        }

        PhaseText.Value = "Forging Token...";

        var expiresUtc = _clock.UtcNow.AddHours(24);

        var identitySigningSpki = _active.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo();

        var oneTimeKey = _oneTimeKeys.PopOneTimeKey();
        if (oneTimeKey is null)
        {
            ErrorText.Value = "Key generator unavailable.";
            return;
        }

        var signedPreKeySpki = oneTimeKey.Value.publicKey.Value;
        var signedPreKeyPriv = oneTimeKey.Value.privateKey.Value;
        var signedPreKeyId = Guid.NewGuid();

        using var ecdsa = ECDsa.Create(_active.Keys.IdentitySigningKey.ExportParameters(true));
        var preKeySignature = ecdsa.SignData(signedPreKeySpki, HashAlgorithmName.SHA256);

        var otk = _oneTimeKeys.PopOneTimeKey();
        if (otk is null)
        {
            ErrorText.Value = "Key generator unavailable.";
            return;
        }

        var otkSpki = otk.Value.publicKey.Value;
        var otkPriv = otk.Value.privateKey.Value;
        var otkId = Guid.NewGuid();

        await _selfPreKeys.SaveSignedPreKeyAsync(
                _active.Identity.SelfIdentityId.Value,
                signedPreKeyId,
                signedPreKeyPriv,
                signedPreKeySpki,
                preKeySignature,
                expiresUtc)
            .ConfigureAwait(false);

        await _selfPreKeys.SaveOneTimePreKeysAsync(
                _active.Identity.SelfIdentityId.Value,
                new[] { (otkId, otkPriv, otkSpki) })
            .ConfigureAwait(false);

        var bundle = new GetPreKeyBundleResponse.Types.PreKeyBundle
        {
            Version = 1,
            IdentityKey = ByteString.CopyFrom(identitySigningSpki),
            SignedPreKeyId = ByteString.CopyFrom(signedPreKeyId.ToByteArray()),
            SignedPreKey = ByteString.CopyFrom(signedPreKeySpki),
            PreKeySignature = ByteString.CopyFrom(preKeySignature),
            OneTimeKeyId = ByteString.CopyFrom(otkId.ToByteArray()),
            OneTimeKey = ByteString.CopyFrom(otkSpki)
        };

        var token = EncodeToken(bundle.ToByteArray());
        GeneratedTokenText.Value = token;
        PhaseText.Value = null;
    }

    private async Task ExecuteImportTokenAsync()
    {
        ResetStatus();

        PhaseText.Value = "Decoding Token...";

        GetPreKeyBundleResponse.Types.PreKeyBundle bundle;
        try
        {
            var bytes = DecodeTokenToBytes(InviteTokenText.Value);
            bundle = GetPreKeyBundleResponse.Types.PreKeyBundle.Parser.ParseFrom(bytes);
        }
        catch
        {
            ErrorText.Value = "Invalid token.";
            PhaseText.Value = null;
            return;
        }

        DnsEndPoint endpoint;
        try
        {
            endpoint = ParseDnsEndPoint(TargetEndpointText.Value);
        }
        catch (Exception ex)
        {
            ErrorText.Value = ex.Message;
            PhaseText.Value = null;
            return;
        }

        PhaseText.Value = "Initiating Handshake...";

        try
        {
            await InitiateStandardHandshakeAsync(bundle, endpoint, CancellationToken.None).ConfigureAwait(false);
            PhaseText.Value = null;
        }
        catch (Exception ex)
        {
            ErrorText.Value = ex.Message;
            PhaseText.Value = null;
        }
    }

    private async Task PersistRelayEndpointAsync(IdentityPeerId relayPeerId, DnsEndPoint relayEndpoint, CancellationToken ct)
    {
        var profile = await _routingProfiles.GetByIdAsync(new NetworkPeerId(relayPeerId.Value), ct).ConfigureAwait(false)
            ?? new PeerRoutingProfile();
        if (profile.Id is null)
        {
            profile.BindIdentity(new NetworkPeerId(relayPeerId.Value));
        }
        profile.AddGrpcEndPoint(new GrpcEndPoint(relayEndpoint, _clock.UtcNow), _clock.UtcNow);
        await _routingProfiles.UpsertAsync(profile, ct).ConfigureAwait(false);
    }

    private async Task<(IdentityPeerId relayPeerId, DirectSessionId sessionId)?> TryGetOrEstablishDirectSessionToRelayPeerAsync(
        IdentityPeerId relayPeerId,
        DnsEndPoint relayEndpoint,
        CancellationToken ct)
    {
        if (_active.Identity is null)
        {
            throw new InvalidOperationException("Identity not loaded.");
        }

        var existing = await TryGetDirectSessionToRelayPeerAsync(relayPeerId).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        EstablishDirectSessionResponse queued;
        try
        {
            var invite = _reverseSignalInvites.CreateInvite();
            queued = await _grpcSessions.EstablishDirectSessionAsync(relayEndpoint, invite).ConfigureAwait(false);
        }
        catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.Unavailable)
        {
            throw new InvalidOperationException("Relay offline.", rpcEx);
        }

        if (queued.MessageCase != EstablishDirectSessionResponse.MessageOneofCase.Queued
            || queued.Queued is null
            || !queued.Queued.HasRequestCorrelationId
            || string.IsNullOrWhiteSpace(queued.Queued.RequestCorrelationId))
        {
            throw new InvalidOperationException("Relay rejected invite.");
        }

        var timeoutUtc = _clock.UtcNow.AddSeconds(10);
        while (_clock.UtcNow < timeoutUtc)
        {
            ct.ThrowIfCancellationRequested();

            var now = await TryGetDirectSessionToRelayPeerAsync(relayPeerId).ConfigureAwait(false);
            if (now is not null)
            {
                return now;
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Timed out waiting for relay to accept invite.");
    }

    private async Task ExecuteNetworkSearchAsync()
    {
        ResetStatus();

        PhaseText.Value = "Establishing Tunnel...";

        var selectedRelay = SelectedRelay.Value;
        if (selectedRelay?.PeerId is null)
        {
            ErrorText.Value = "Select a known relay.";
            PhaseText.Value = null;
            return;
        }

        DnsEndPoint relayEndpoint;
        try
        {
            relayEndpoint = ParseDnsEndPoint(RelayEndpointText.Value);
        }
        catch
        {
            ErrorText.Value = "Relay endpoint required.";
            PhaseText.Value = null;
            return;
        }

        if (_active.Identity is null)
        {
            ErrorText.Value = "Identity not loaded.";
            PhaseText.Value = null;
            return;
        }

        try
        {
            var directRelay = await TryGetOrEstablishDirectSessionToRelayPeerAsync(
                selectedRelay.PeerId,
                relayEndpoint,
                CancellationToken.None).ConfigureAwait(false);

            await PersistRelayEndpointAsync(selectedRelay.PeerId, relayEndpoint, CancellationToken.None).ConfigureAwait(false);

            PhaseText.Value = "Querying Node...";

            var pkhBytes = ParsePkh(TargetPkhText.Value);

            var env = new InternalEnvelope
            {
                PrekeyEnvelope = new PrekeyEnvelope
                {
                    Version = 1,
                    GetPreKeyBundleRequest = new GetPreKeyBundleRequest
                    {
                        Version = 1,
                        PublicKeyHash = ByteString.CopyFrom(pkhBytes)
                    }
                }
            };

            var plain = new Plaintext(env.ToByteArray());
            var cipher = await _secureMessaging.EncryptAsync(new SessionId(directRelay.Value.sessionId.Value), plain, CancellationToken.None)
                .ConfigureAwait(false);

            var deliver = await _transport.SendMessageAsync(
                    directRelay.Value.relayPeerId,
                    directRelay.Value.sessionId,
                    cipher,
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (deliver.ResultCase != DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload
                || deliver.ResponsePayload is null
                || !deliver.ResponsePayload.HasResponsePayload
                || deliver.ResponsePayload.ResponsePayload.Length == 0)
            {
                ErrorText.Value = "Relay did not respond.";
                PhaseText.Value = null;
                return;
            }

            PhaseText.Value = "Decrypting Bundle...";

            var respCipher = new SessionRatchetMessage(deliver.ResponsePayload.ResponsePayload.ToByteArray());
            var resolved = await _secureMessaging.DecryptInboundAsync(_active.Identity.SelfIdentityId.Value, respCipher, CancellationToken.None)
                .ConfigureAwait(false);

            if (resolved is null)
            {
                ErrorText.Value = "Failed to decrypt relay response.";
                PhaseText.Value = null;
                return;
            }

            var respEnv = InternalEnvelope.Parser.ParseFrom(resolved.Value.plaintext.Value);
            if (respEnv.ApplicationPayloadCase != InternalEnvelope.ApplicationPayloadOneofCase.GetPreKeyBundleResponse)
            {
                ErrorText.Value = "Unexpected relay response.";
                PhaseText.Value = null;
                return;
            }

            if (respEnv.GetPreKeyBundleResponse?.PreKeyBundle is null)
            {
                ErrorText.Value = "Target not found.";
                PhaseText.Value = null;
                return;
            }

            PhaseText.Value = "Initiating Handshake...";

            // For Network Search, use the target endpoint field as the destination (fallback).
            var targetEndpoint = ParseDnsEndPoint(TargetEndpointText.Value);
            await InitiateStandardHandshakeAsync(respEnv.GetPreKeyBundleResponse.PreKeyBundle, targetEndpoint, CancellationToken.None)
                .ConfigureAwait(false);

            PhaseText.Value = null;
        }
        catch (Exception ex)
        {
            ErrorText.Value = ex.Message;
            PhaseText.Value = null;
        }
    }

    private async Task InitiateStandardHandshakeAsync(
        GetPreKeyBundleResponse.Types.PreKeyBundle bundle,
        DnsEndPoint endpoint,
        CancellationToken ct)
    {
        if (_active.Identity is null || _active.Keys?.IdentitySigningKey is null)
        {
            throw new InvalidOperationException("Active identity not loaded.");
        }

        if (!bundle.HasIdentityKey || bundle.IdentityKey.Length == 0)
            throw new InvalidOperationException("Token missing identity key.");
        if (!bundle.HasSignedPreKeyId || bundle.SignedPreKeyId.Length == 0)
            throw new InvalidOperationException("Token missing signed pre-key id.");
        if (!bundle.HasSignedPreKey || bundle.SignedPreKey.Length == 0)
            throw new InvalidOperationException("Token missing signed pre-key.");
        if (!bundle.HasPreKeySignature || bundle.PreKeySignature.Length == 0)
            throw new InvalidOperationException("Token missing signature.");

        var remoteIdentitySpki = bundle.IdentityKey.ToByteArray();
        var remotePkh = SHA256.HashData(remoteIdentitySpki);

        var remotePeerId = await _peerKeyStore.GetPeerIdByPublicKeyHashAsync(remotePkh, ct).ConfigureAwait(false);
        if (remotePeerId is null)
        {
            var hex = Convert.ToHexString(remotePkh);
            var newId = IdentityPeerId.NewId();
            var identity = new PeerIdentity(newId);
            identity.SetDisplayName(new DisplayName($"Peer-{hex.Substring(0, Math.Min(12, hex.Length))}"));
            await _peerIdentities.SaveAsync(identity, ct).ConfigureAwait(false);
            await _peerKeyStore.ActivateIfChangedAsync(newId, remoteIdentitySpki, remotePkh, _clock.UtcNow, ct).ConfigureAwait(false);
            remotePeerId = newId;
        }

        Guid signedPreKeyId;
        try
        {
            signedPreKeyId = new Guid(bundle.SignedPreKeyId.ToByteArray());
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Signed pre-key id invalid.", ex);
        }

        Guid? oneTimePreKeyId = null;
        OneTimeKey? oneTimePreKey = null;
        if (bundle.HasOneTimeKeyId && bundle.OneTimeKeyId.Length > 0 && bundle.HasOneTimeKey && bundle.OneTimeKey.Length > 0)
        {
            try
            {
                oneTimePreKeyId = new Guid(bundle.OneTimeKeyId.ToByteArray());
                oneTimePreKey = new OneTimeKey(bundle.OneTimeKey.ToByteArray());
            }
            catch
            {
                oneTimePreKeyId = null;
                oneTimePreKey = null;
            }
        }

        var remoteIdentity = new RatchetIdentityKey(remoteIdentitySpki);
        var remoteSpk = new PreKey(bundle.SignedPreKey.ToByteArray());
        var remoteSig = new Percolator.Cryptography.Signature(bundle.PreKeySignature.ToByteArray());

        if (!_sessionCrypto.VerifySignature(remoteIdentity, remoteSpk, remoteSig))
        {
            throw new InvalidOperationException("Invite token signature invalid.");
        }

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

        var req = new EstablishSessionRequest
        {
            Version = 1,
            IdentitySigningKey = ByteString.CopyFrom(_active.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
            EphemeralKey = ByteString.CopyFrom(x3.EphemeralPublic.Value),
            PrekeyId = ByteString.CopyFrom(signedPreKeyId.ToByteArray())
        };
        if (oneTimePreKeyId is not null)
        {
            req.OnetimePrekeyId = ByteString.CopyFrom(oneTimePreKeyId.Value.ToByteArray());
        }

        var resp = await _grpcSessions.EstablishSessionAsync(endpoint, req, ct).ConfigureAwait(false);
        if (resp.Response is null || !resp.Response.HasResponsePayload || resp.Response.ResponsePayload.Length == 0)
        {
            throw new InvalidOperationException("Handshake failed.");
        }

        EstablishSessionResponse.Types.Response.Types.ResponsePayload respPayload;
        try
        {
            respPayload = EstablishSessionResponse.Types.Response.Types.ResponsePayload.Parser.ParseFrom(resp.Response.ResponsePayload);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Handshake response invalid.", ex);
        }

        if (!respPayload.HasSessionId || string.IsNullOrWhiteSpace(respPayload.SessionId))
            throw new InvalidOperationException("Handshake response missing session id.");

        var sessionId = new SessionId(Guid.Parse(respPayload.SessionId));

        var root = new RootKey(x3.SharedSecret.Value);
        var initiatorSession = RatchetBootstrap.CreateInitiatorSession(
            sessionId,
            new Percolator.Cryptography.Primitives.PeerId(remotePeerId.Value),
            new ProtocolVersion(1),
            root,
            _clock,
            crypto: _sessionCrypto);

        await _sessions.AddAsync(initiatorSession, ct).ConfigureAwait(false);

        await _directSessions.UpsertAsync(
                new NetworkPeerId(remotePeerId.Value),
                new Percolator.Network.DirectSessionId(sessionId.Value),
                _active.Identity.SelfIdentityId.Value)
            .ConfigureAwait(false);

        var profile = await _routingProfiles.GetByIdAsync(new NetworkPeerId(remotePeerId.Value), ct).ConfigureAwait(false)
            ?? new PeerRoutingProfile();
        if (profile.Id is null)
        {
            profile.BindIdentity(new NetworkPeerId(remotePeerId.Value));
        }
        profile.AddGrpcEndPoint(new GrpcEndPoint(endpoint, _clock.UtcNow), _clock.UtcNow);
        profile.SetIdentityPublicKey(new IdentityPublicKey(remoteIdentitySpki));
        await _routingProfiles.UpsertAsync(profile, ct).ConfigureAwait(false);
    }

    private async Task<(IdentityPeerId relayPeerId, Percolator.Network.DirectSessionId sessionId)?> TryGetDirectSessionToRelayPeerAsync(IdentityPeerId relayPeerId)
    {
        if (_active.Identity is null)
        {
            return null;
        }

        var ds = await _directSessions.GetByRemotePeerIdAsync(
                new NetworkPeerId(relayPeerId.Value),
                _active.Identity.SelfIdentityId.Value)
            .ConfigureAwait(false);

        return ds is null ? null : (relayPeerId, ds.SessionId);
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

    private static byte[] ParsePkh(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Target PKH required.");

        var t = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            t = t.Substring(2);

        if (TryParseHex(t, out var hexBytes))
            return hexBytes;

        try
        {
            return Convert.FromBase64String(t);
        }
        catch
        {
            throw new InvalidOperationException("PKH must be hex or base64.");
        }
    }

    private static bool TryParseHex(string text, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length % 2 != 0) return false;

        try
        {
            bytes = Convert.FromHexString(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string EncodeToken(byte[] bytes)
        => Convert.ToBase64String(bytes);

    private static byte[] DecodeTokenToBytes(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Token required.");
        }

        var t = new string(token.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return Convert.FromBase64String(t);
    }

    private static string ToShortHex(byte[] bytes)
    {
        var hex = Convert.ToHexString(bytes);
        if (hex.Length <= 8)
        {
            return "0x" + hex;
        }
        return "0x" + hex.Substring(0, 4) + "..." + hex.Substring(hex.Length - 4);
    }

    private void ResetStatus()
    {
        PhaseText.Value = null;
        ErrorText.Value = null;
    }

    protected override void DisposeCore()
    {
        _bag.Dispose();
    }
}
