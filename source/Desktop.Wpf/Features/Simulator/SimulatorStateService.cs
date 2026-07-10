using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ObservableCollections;
using Percolator.Application.Configuration;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using R3;
using Desktop.Wpf.Features.Simulator.Models;
using System.Security.Cryptography;
using Desktop.Wpf.Features.Simulator.Tracking;
using System.Collections.Concurrent;
using System.Net;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorStateService : ISimulatorStateService, ISimulatorStateInitializer
{
    private readonly ISimulatorStateRepository _store;
    private readonly ISimulatorDiagnosticsService _diagnostics;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Desktop.Wpf.Features.Simulator.Protocol.ISignalProtocolEngine _engine;
    private readonly IOptions<TransportOptions> _transportOptions;
    private readonly ISimulatorToMainTransportService _toMain;

    private readonly Subject<Unit> _saveTrigger = new();
    private DisposableBag _bag;

    private readonly ObservableList<SimulatedPeerModel> _peers = new();
    public IReadOnlyObservableList<SimulatedPeerModel> Peers => _peers;

    private readonly ObservableList<SimulatedRelayModel> _relays = new();
    public IReadOnlyObservableList<SimulatedRelayModel> Relays => _relays;

    private readonly ObservableList<PeerRelationship> _relationships = new();
    public IReadOnlyObservableList<PeerRelationship> Relationships => _relationships;

    private readonly Dictionary<Percolator.Network.NetworkPeerId, SimulatedRelayModel> _relayByHostPeerId = new();

    private readonly Dictionary<Percolator.Network.NetworkPeerId, SimulatedPeerModel> _peerById = new();

    private readonly ConcurrentDictionary<DnsEndPoint, Percolator.Network.NetworkPeerId> _endpointIndex = new();

    private readonly Dictionary<Percolator.Network.NetworkPeerId, IDisposable> _runtimePersistenceByPeerId = new();

    private readonly Dictionary<Percolator.Network.NetworkPeerId, IDisposable> _relayPersistenceByHostPeerId = new();

    private List<GroupConversationDto> _groups = new();

    private int _nextSelfIdentityId = 99000 - 1;

    private readonly object _initGate = new();
    private Task? _initializeTask;

    private readonly SemaphoreSlim _stateGate = new(1, 1);

    private readonly TimeProvider _timeProvider;

    public SimulatorStateService(
        ISimulatorStateRepository store,
        ISimulatorDiagnosticsService diagnostics,
        IServiceScopeFactory scopeFactory,
        ISimulatorToMainTransportService toMain,
        IOptions<TransportOptions> transportOptions,
        Desktop.Wpf.Features.Simulator.Protocol.ISignalProtocolEngine engine,
        TimeProvider timeProvider)
    {
        _store = store;
        _diagnostics = diagnostics;
        _scopeFactory = scopeFactory;
        _toMain = toMain;
        _transportOptions = transportOptions;
        _engine = engine;

        _timeProvider = timeProvider;

        _saveTrigger
            .Debounce(TimeSpan.FromMilliseconds(250), _timeProvider)
            .SelectAwait(async (_, ct) => await FreezeSnapshotAsync(ct).ConfigureAwait(false))
            .SubscribeAwait(async (snap, ct) => await _store.SaveStateAsync(snap, ct).ConfigureAwait(false), AwaitOperation.Sequential)
            .AddTo(ref _bag);
    }

    private async Task<T> WithStateGateAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task WithStateGateAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task<SimulatorStateSnapshot> FreezeSnapshotAsync(CancellationToken cancellationToken)
    {
        return await WithStateGateAsync(async () =>
        {
            var peerSnaps = _peers.Select(p => p.Freeze()).ToList();
            var relSnaps = _relationships
                .Select(r => new PeerRelationshipSnapshot(r.SourceNetworkPeerId, r.TargetNetworkPeerId, r.Type))
                .ToList();
            var relaySnaps = _relays.Select(r => r.Freeze()).ToList();

            return new SimulatorStateSnapshot(
                Version: 1,
                Peers: peerSnaps,
                Relationships: relSnaps,
                Relays: relaySnaps,
                Groups: _groups.ToList());
        }, cancellationToken).ConfigureAwait(false);
    }

    private IClock ResolveClock()
    {
        // SimulatorStateService is singleton; obtain a scoped clock instance when needed.
        using var scope = _scopeFactory.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IClock>();
    }

    public async Task<EstablishDirectSessionResponse> ReceiveEstablishDirectSessionFromMainAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        Percolator.Network.NetworkPeerId mainNetworkPeerId,
        EstablishDirectSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null) throw new ArgumentNullException(nameof(request));

        // Chunk B8.4: Validate request fields before parsing
        if (!request.HasPayload || request.Payload.Length == 0)
            throw new InvalidOperationException("Invite missing payload");
        if (!request.HasInviterIdentityKey || request.InviterIdentityKey.Length == 0)
            throw new InvalidOperationException("Invite missing inviter_identity_key");

        // Chunk B: Parse correlation id and inviter identity key from request
        var payload = InviteHandshakeRequestPayload.Parser.ParseFrom(request.Payload);
        if (!Guid.TryParse(payload.RequestCorrelationId, out var correlationId))
        {
            throw new InvalidOperationException("Invalid correlation id in request");
        }

        var inviterIdentityKeySpki = request.InviterIdentityKey.ToByteArray();

        // Chunk B: Persist the inbound request as pending (do NOT create session yet)
        await WithStateGateAsync(() =>
        {
            var peer = _peers.FirstOrDefault(p => p.NetworkPeerId == simulatedNetworkPeerId);
            if (peer is null)
            {
                throw new InvalidOperationException($"Simulated peer {simulatedNetworkPeerId} not found");
            }

            peer.AddPendingInboundDirectInvite(correlationId, request.ToByteArray(), inviterIdentityKeySpki, mainNetworkPeerId);

            // Transition UI state to indicate pending inbound invite request
            peer.SetUiState(SimulatorPeerUiState.AwaitingUserAcceptance);
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);

        // Chunk B: Return Queued without creating a session
        return new EstablishDirectSessionResponse
        {
            Version = 1,
            Queued = new EstablishDirectSessionResponse.Types.Queued
            {
                Version = 1,
                RequestCorrelationId = correlationId.ToString()
            }
        };
    }

    public async Task AcceptPendingInboundDirectInviteAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var (request, inviterPeerId) = await WithStateGateAsync(() =>
        {
            var peer = _peers.FirstOrDefault(p => p.NetworkPeerId == simulatedNetworkPeerId)
                ?? throw new InvalidOperationException($"Simulated peer {simulatedNetworkPeerId} not found");

            if (!peer.TryTakePendingInboundDirectInvite(correlationId, out var pendingInvite))
            {
                throw new InvalidOperationException($"No pending inbound direct invite found with correlation id {correlationId}");
            }

            var req = EstablishDirectSessionRequest.Parser.ParseFrom(pendingInvite.RequestBytes);
            var inviter = pendingInvite.InviterNetworkPeerId; // Use persisted inviter peer id (Main)
            return Task.FromResult((req, inviter));
        }, cancellationToken).ConfigureAwait(false);

        // Accept the invite using the existing logic
        var acceptance = await AcceptInboundDirectInviteAsync(simulatedNetworkPeerId, inviterPeerId, request, cancellationToken)
            .ConfigureAwait(false);

        // B7: Deliver response immediately instead of queuing (remove queued response mechanism)
        _ = await _toMain
            .DeliverInviteHandshakeResponseToMainAsync(acceptance.Response, cancellationToken)
            .ConfigureAwait(false);

        // Clear the pending state and select next oldest if any
        await WithStateGateAsync(() =>
        {
            var peer = _peers.FirstOrDefault(p => p.NetworkPeerId == simulatedNetworkPeerId)
                ?? throw new InvalidOperationException($"Simulated peer {simulatedNetworkPeerId} not found");

            peer.SetUiState(peer.PendingInboundDirectInvites.Count > 0
                ? SimulatorPeerUiState.AwaitingUserAcceptance
                : SimulatorPeerUiState.Ready);
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RejectPendingInboundDirectInviteAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await WithStateGateAsync(() =>
        {
            var peer = _peers.FirstOrDefault(p => p.NetworkPeerId == simulatedNetworkPeerId)
                ?? throw new InvalidOperationException($"Simulated peer {simulatedNetworkPeerId} not found");

            peer.TryTakePendingInboundDirectInvite(correlationId, out _);

            peer.SetUiState(peer.PendingInboundDirectInvites.Count > 0
                ? SimulatorPeerUiState.AwaitingUserAcceptance
                : SimulatorPeerUiState.Ready);
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SimulatedPeerInviteAcceptance> AcceptInboundDirectInviteAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        Percolator.Network.NetworkPeerId inviterNetworkPeerId,
        EstablishDirectSessionRequest invite,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (invite is null) throw new ArgumentNullException(nameof(invite));

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var model = _peers.FirstOrDefault(p => p.NetworkPeerId == simulatedNetworkPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedNetworkPeerId}");

        if (!invite.HasInviterIdentityKey || invite.InviterIdentityKey.Length == 0)
            throw new InvalidOperationException("Invite missing inviter_identity_key");
        if (!invite.HasPayload || invite.Payload.Length == 0)
            throw new InvalidOperationException("Invite missing payload");

        var payload = InviteHandshakeRequestPayload.Parser.ParseFrom(invite.Payload);
        if (payload.InviterPreKey is null)
            throw new InvalidOperationException("Invite payload missing inviter_pre_key");
        if (!payload.InviterPreKey.HasInviterSignedPreKey || payload.InviterPreKey.InviterSignedPreKey.Length == 0)
            throw new InvalidOperationException("Invite payload missing inviter_signed_pre_key");
        if (!payload.InviterPreKey.HasPreKeySignature || payload.InviterPreKey.PreKeySignature.Length == 0)
            throw new InvalidOperationException("Invite payload missing pre_key_signature");
        if (!payload.HasRequestCorrelationId || string.IsNullOrWhiteSpace(payload.RequestCorrelationId))
            throw new InvalidOperationException("Invite payload missing request_correlation_id");

        OneTimeKey? inviterOtk = null;
        if (payload.InviterPreKey.HasInviterOneTimePreKey && payload.InviterPreKey.InviterOneTimePreKey.Length > 0)
        {
            inviterOtk = OneTimeKey.FromBytesOwned(payload.InviterPreKey.InviterOneTimePreKey.ToByteArray());
        }

        var inviterBundle = new Percolator.Cryptography.PreKeyBundle(
            identitySigningKey: RatchetIdentityKey.FromBytesOwned(invite.InviterIdentityKey.ToByteArray()),
            signedPreKeyId: Guid.Empty,
            signedPreKey: PreKey.FromBytesOwned(payload.InviterPreKey.InviterSignedPreKey.ToByteArray()),
            signedPreKeySignature: Percolator.Cryptography.Signature.FromBytesOwned(payload.InviterPreKey.PreKeySignature.ToByteArray()),
            oneTimePreKeyId: null,
            oneTimePreKey: inviterOtk,
            expirationDateUtc: payload.ExpiresAtUtc?.ToDateTimeOffset());

            var crypto = new AeadSessionCrypto();
            var clock = ResolveClock();

            var localIkPriv = PrivatePreKey.FromBytes(model.IdentitySigningKeyPrivateKeyEcPrivateKey);
            var x3 = crypto.X3DH_Initiate(localIkPriv, inviterBundle);

            var sessionId = SessionId.NewId();
            var root = RootKey.FromBytes(x3.SharedSecret.ToArray());
            var session = RatchetBootstrap.CreateInitiatorSession(
                sessionId,
                new Percolator.Cryptography.Primitives.PeerId(inviterNetworkPeerId.Value),
                new ProtocolVersion(1),
                root,
                clock,
                crypto: crypto);

            model.SessionsMutable[sessionId] = session;

            var inner = new ResponderInnerHello
            {
                Version = 1,
                DirectSessionId = sessionId.Value.ToString(),
                PublicIdentityId = ByteString.CopyFrom( model.PublicIdentityId.Value.ToByteArray())
            };

            var initial = session.Encrypt(Plaintext.FromBytesOwned(inner.ToByteArray()), clock);
            model.SessionsMutable[sessionId] = session;

            var response = new InviteHandshakeResponse
            {
                Version = 1,
                RequestCorrelationId = payload.RequestCorrelationId,
                AcceptorIdentityKey = ByteString.CopyFrom(model.IdentitySigningKeySpki),
                AcceptorX3DhEphemeralKey = ByteString.CopyFrom(x3.EphemeralPublic.ToArray()),
                InitialRatchetMessage = ByteString.CopyFrom(initial.ToArray()),
                AcceptorPublicIdentityId = ByteString.CopyFrom(model.PublicIdentityId.Value.ToByteArray())
            };

            return new SimulatedPeerInviteAcceptance(sessionId, response);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task HandleInboundInviteHandshakeResponseFromMainAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (response is null) throw new ArgumentNullException(nameof(response));

        if (!Guid.TryParse(response.RequestCorrelationId, out var correlationId))
        {
            throw new InvalidOperationException("InviteHandshakeResponse missing or invalid request_correlation_id");
        }

        // Validate required fields
        if (!response.HasAcceptorIdentityKey || response.AcceptorIdentityKey.Length == 0)
            throw new InvalidOperationException("InviteHandshakeResponse missing acceptor_identity_key");
        if (!response.HasAcceptorX3DhEphemeralKey || response.AcceptorX3DhEphemeralKey.Length == 0)
            throw new InvalidOperationException("InviteHandshakeResponse missing acceptor_x3dh_ephemeral_key");
        if (!response.HasInitialRatchetMessage || response.InitialRatchetMessage.Length == 0)
            throw new InvalidOperationException("InviteHandshakeResponse missing initial_ratchet_message");

        SimulatedPeerModel model;
        SimulatedOutboundInviteModel? outbound;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            model = _peers.FirstOrDefault(p => p.NetworkPeerId == simulatedNetworkPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedNetworkPeerId}");

            outbound = model.OutboundInvitesMutable.FirstOrDefault(x => x.CorrelationId == correlationId);
            if (outbound is null)
            {
                // Response does not match any outbound invite - this is unexpected
                _diagnostics.Emit(
                    SimulatorDiagnosticEventType.HandshakeError,
                    $"InviteHandshakeResponse with correlation {correlationId} does not match any outbound invite",
                    peerId: simulatedNetworkPeerId,
                    contextTag: "ResponseNoMatchingOutboundInvite");
                return;
            }
        }
        finally
        {
            _stateGate.Release();
        }

        // Perform X3DH_Respond and finalize session
        var acceptorIdentityPublic = RatchetIdentityKey.FromBytesOwned(response.AcceptorIdentityKey.ToByteArray());
        var acceptorEphemeralPublic = RatchetEphemeralKey.FromBytesOwned(response.AcceptorX3DhEphemeralKey.ToByteArray());

        var crypto = new AeadSessionCrypto();
        var localIkPriv = PrivatePreKey.FromBytes(model.IdentitySigningKeyPrivateKeyEcPrivateKey);
        var localSpkPriv = PrivatePreKey.FromBytes(outbound.SignedPreKeyPrivateEcPrivateKey);

        SharedSecret shared;
        try
        {
            shared = crypto.X3DH_Respond(
                acceptorIdentityPublic,
                acceptorEphemeralPublic,
                localIkPriv,
                localSpkPriv,
                localOtkPrivate: null);
        }
        catch (Exception ex)
        {
            _diagnostics.Emit(
                SimulatorDiagnosticEventType.HandshakeError,
                $"X3DH_Respond failed for correlation {correlationId}: {ex.Message}",
                peerId: simulatedNetworkPeerId,
                contextTag: "X3DHRespondFailed");
            return;
        }

        var root = RootKey.FromSpan(shared.Span);

        SessionRatchetMessage ratchetMessage;
        try
        {
            ratchetMessage = SessionRatchetMessage.FromBytesOwned(response.InitialRatchetMessage.ToByteArray());
        }
        catch (Exception ex)
        {
            _diagnostics.Emit(
                SimulatorDiagnosticEventType.HandshakeError,
                $"Failed to parse ratchet message for correlation {correlationId}: {ex.Message}",
                peerId: simulatedNetworkPeerId,
                contextTag: "RatchetMessageParseFailed");
            return;
        }

        var clock = ResolveClock();
        var tmp = RatchetBootstrap.CreateResponderSession(
            SessionId.NewId(),
            new Percolator.Cryptography.Primitives.PeerId(simulatedNetworkPeerId.Value),
            new ProtocolVersion(1),
            root,
            clock);

        Plaintext pt;
        try
        {
            pt = tmp.Decrypt(ratchetMessage, clock);
        }
        catch (Exception ex)
        {
            _diagnostics.Emit(
                SimulatorDiagnosticEventType.HandshakeError,
                $"Failed to decrypt ratchet message for correlation {correlationId}: {ex.Message}",
                peerId: simulatedNetworkPeerId,
                contextTag: "RatchetMessageDecryptFailed");
            return;
        }

        ResponderInnerHello inner;
        try
        {
            inner = ResponderInnerHello.Parser.ParseFrom(pt.ToArray());
        }
        catch (Exception ex)
        {
            _diagnostics.Emit(
                SimulatorDiagnosticEventType.HandshakeError,
                $"Failed to parse ResponderInnerHello for correlation {correlationId}: {ex.Message}",
                peerId: simulatedNetworkPeerId,
                contextTag: "ResponderInnerHelloParseFailed");
            return;
        }

        if (!inner.HasVersion || inner.Version != 1)
        {
            _diagnostics.Emit(
                SimulatorDiagnosticEventType.HandshakeError,
                $"Invalid ResponderInnerHello version for correlation {correlationId}",
                peerId: simulatedNetworkPeerId,
                contextTag: "InvalidResponderInnerHelloVersion");
            return;
        }
        if (!inner.HasDirectSessionId || string.IsNullOrWhiteSpace(inner.DirectSessionId))
        {
            _diagnostics.Emit(
                SimulatorDiagnosticEventType.HandshakeError,
                $"Missing DirectSessionId in ResponderInnerHello for correlation {correlationId}",
                peerId: simulatedNetworkPeerId,
                contextTag: "MissingDirectSessionId");
            return;
        }

        SessionId sessionId;
        try
        {
            sessionId = new SessionId(Guid.Parse(inner.DirectSessionId));
        }
        catch (Exception ex)
        {
            _diagnostics.Emit(
                SimulatorDiagnosticEventType.HandshakeError,
                $"Invalid DirectSessionId format for correlation {correlationId}: {ex.Message}",
                peerId: simulatedNetworkPeerId,
                contextTag: "InvalidDirectSessionIdFormat");
            return;
        }

        var final = SecureSession.Create(
            sessionId,
            tmp.RemotePeerId,
            tmp.ProtocolVersion,
            tmp.State,
            crypto,
            clock);

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Remove the outbound invite (it's now completed)
            model.OutboundInvitesMutable.Remove(outbound);

            // Store the session
            model.SessionsMutable[sessionId] = final;

            // Transition UI state to Established
            model.MarkEstablished();

            _diagnostics.Emit(
                SimulatorDiagnosticEventType.HandshakeStateTransition,
                $"Handshake: established corr={correlationId.ToString()[..8]} sessionId={sessionId.Value.ToString()[..8]}",
                peerId: simulatedNetworkPeerId,
                contextTag: "Established");
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task<EstablishSessionResponse> ReceiveEstablishSessionFromMainAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        EstablishSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null) throw new ArgumentNullException(nameof(request));

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var model = _peers.FirstOrDefault(p => p.NetworkPeerId == simulatedNetworkPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedNetworkPeerId}");

        if (!request.HasIdentitySigningKey || request.IdentitySigningKey.Length == 0)
            throw new InvalidOperationException("EstablishSession missing identity_signing_key");
        if (!request.HasEphemeralKey || request.EphemeralKey.Length == 0)
            throw new InvalidOperationException("EstablishSession missing ephemeral_key");
        if (!request.HasPrekeyId || request.PrekeyId.Length == 0)
            throw new InvalidOperationException("EstablishSession missing prekey_id");

        Guid spkId;
        try
        {
            spkId = new Guid(request.PrekeyId.ToByteArray());
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("EstablishSession prekey_id must be GUID bytes", ex);
        }

        var spk = model.SignedPreKeysMutable.FirstOrDefault(x => x.SignedPreKeyId == spkId);
        if (spk is null)
        {
           throw new InvalidOperationException($"could not find signed prekey by signed prekey id");
        }

        var initiatorId = RatchetIdentityKey.FromBytesOwned(request.IdentitySigningKey.ToByteArray());
        var initiatorEph = RatchetEphemeralKey.FromBytesOwned(request.EphemeralKey.ToByteArray());

        var crypto = new AeadSessionCrypto();
        var localIkPriv = PrivatePreKey.FromBytes(model.IdentitySigningKeyPrivateKeyEcPrivateKey);
        var localSpkPriv = PrivatePreKey.FromBytes(spk.PrivateEcPrivateKey);

        PrivatePreKey localOtkPrivate;
        if (request.HasOnetimePrekeyId && request.OnetimePrekeyId.Length > 0)
        {
            var otkGuid = new Guid(request.OnetimePrekeyId.ToByteArray());
            var otkId = SimulatedOneTimePreKeyId.FromGuid(otkGuid);
            if (!model.TryPopOneTimePreKeyPrivate(otkId, out localOtkPrivate))
            {
                throw new InvalidOperationException(
                    $"Requested OTK id {otkGuid} not found on simulated peer {simulatedNetworkPeerId}. " +
                    $"Remaining private OTK count: {model.OneTimePreKeysPrivate.Count}. " +
                    $"This indicates a mismatch between initiator and responder OTK pools.");
            }
        }
        else
        {
            localOtkPrivate = null;
        }

        var shared = crypto.X3DH_Respond(
            initiatorId,
            initiatorEph,
            localIkPriv,
            localSpkPriv,
            localOtkPrivate);

        var sessionId = SessionId.NewId();
        var root = RootKey.FromSpan(shared.Span);
        var clock = ResolveClock();
        var session = RatchetBootstrap.CreateResponderSession(
            sessionId,
            new Percolator.Cryptography.Primitives.PeerId((uint)Random.Shared.Next()),
            new ProtocolVersion(1),
            root,
            clock,
            crypto: crypto);

        model.SessionsMutable[sessionId] = session;

        var responsePayload = new EstablishSessionResponse.Types.Response.Types.ResponsePayload
        {
            Version = 1,
            EphemeralKey = ByteString.CopyFrom(spk.PublicSpki),
            SessionId = sessionId.Value.ToString()
        };

        var payloadBytes = responsePayload.ToByteArray();

        using var identityEcdh2 = ECDiffieHellman.Create();
        identityEcdh2.ImportECPrivateKey(model.IdentitySigningKeyPrivateKeyEcPrivateKey, out _);
        using var identityEcdsa = ECDsa.Create(identityEcdh2.ExportParameters(true));
        var sig = identityEcdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);

            return new EstablishSessionResponse
            {
                Version = 1,
                Response = new EstablishSessionResponse.Types.Response
                {
                    Version = 1,
                    IdentitySigningKey = ByteString.CopyFrom(model.IdentitySigningKeySpki),
                    ResponsePayload = ByteString.CopyFrom(payloadBytes),
                    PayloadSignature = ByteString.CopyFrom(sig)
                }
            };
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task<DeliverOpaqueMessageResponse> ReceiveOpaqueMessageFromMainAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        DeliverOpaqueMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null) throw new ArgumentNullException(nameof(request));

        SimulatedPeerModel model;
        Plaintext? pt;
        SecureSession? matched;
        SessionId? matchedSessionId;

        var clock = ResolveClock();
        // Phase 1: only hold the gate while we touch peer/session state.
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            model = _peers.FirstOrDefault(p => p.NetworkPeerId == simulatedNetworkPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedNetworkPeerId}");

            // Best-effort: try to decrypt with any known session (typically 1 per peer in simulator today)
            var cipher = SessionRatchetMessage.FromBytesOwned(request.Payload.ToByteArray());
            

            pt = null;
            matched = null;
            matchedSessionId = null;

            foreach (var kv in model.SessionsMutable)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var candidate = kv.Value;
                    var candidatePt = candidate.Decrypt(cipher, clock);
                    if (candidatePt.ToArray().Length == 0)
                    {
                        model.SessionsMutable[kv.Key] = candidate;
                        return new DeliverOpaqueMessageResponse { Version = 1 };
                    }

                    pt = candidatePt;
                    matched = candidate;
                    matchedSessionId = kv.Key;
                    model.SessionsMutable[kv.Key] = candidate;
                    break;
                }
                catch
                {
                    // not this session
                }
            }
        }
        finally
        {
            _stateGate.Release();
        }

        if (pt is null || matched is null || matchedSessionId is null)
        {
            return new DeliverOpaqueMessageResponse { Version = 1 };
        }

        InternalEnvelope env;
        try
        {
            env = InternalEnvelope.Parser.ParseFrom(pt.ToArray());
        }
        catch
        {
            return new DeliverOpaqueMessageResponse { Version = 1, Never = new DeliverOpaqueMessageResponse.Types.Never { Version = 1 } };
        }

        if (env.ApplicationPayloadCase == InternalEnvelope.ApplicationPayloadOneofCase.ChatEnvelope
            && env.ChatEnvelope?.MessageCase == ChatEnvelope.MessageOneofCase.TextMessage)
        {
            var text = env.ChatEnvelope.TextMessage;
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                model.AddChatMessage(
                    isFromMain: true,
                    content: text.Content,
                    receivedUtc: text.SentTimestampUtc?.ToDateTimeOffset() ?? _timeProvider.GetUtcNow());
            }
            finally
            {
                _stateGate.Release();
            }
            _saveTrigger.OnNext(Unit.Default);
            return new DeliverOpaqueMessageResponse { Version = 1 };
        }

        if (env.ApplicationPayloadCase == InternalEnvelope.ApplicationPayloadOneofCase.PrekeyEnvelope
            && env.PrekeyEnvelope?.MessageCase == PrekeyEnvelope.MessageOneofCase.GetPreKeyBundleRequest)
        {
            var getReq = env.PrekeyEnvelope.GetPreKeyBundleRequest;
            if (!getReq.HasPublicKeyHash || getReq.PublicKeyHash.Length == 0)
            {
                throw new InvalidOperationException("GetPreKeyBundleRequest missing public_key_hash");
            }

            SimulatedPublishedPreKeyBundleModel? popped;
            try
            {
                popped = await TryPopPreKeyBundleByRecipientPkhAsync(
                        simulatedNetworkPeerId,
                        Percolator.Identity.IdentityPublicKeyHash.FromBytesOwned(getReq.PublicKeyHash.ToByteArray()),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                return new DeliverOpaqueMessageResponse { Version = 1, Never = new DeliverOpaqueMessageResponse.Types.Never { Version = 1 } };
            }

            if (popped is null)
            {
                return new DeliverOpaqueMessageResponse { Version = 1, Never = new DeliverOpaqueMessageResponse.Types.Never { Version = 1 } };
            }

            // Serialize bundle from individual components
            var bundleDto = new GetPreKeyBundleResponse.Types.PreKeyBundle
            {
                Version = 1,
                IdentityKey = ByteString.CopyFrom(popped.IdentityKey),
                SignedPreKeyId = ByteString.CopyFrom(popped.SignedPreKeyId.ToByteArray()),
                SignedPreKey = ByteString.CopyFrom(popped.SignedPreKey),
                PreKeySignature = ByteString.CopyFrom(popped.PreKeySignature)
            };

            //there should be 0-1 onetime keys
            var onetimekey = popped.OneTimeKeys.FirstOrDefault();
            if (onetimekey is not null)
            {
                bundleDto.OneTimeKeys.Add(new GetPreKeyBundleResponse.Types.OneTimeKey
                {
                    OneTimeKeyId = ByteString.CopyFrom(onetimekey.Id.ToByteArray()),
                    KeyBytes = ByteString.CopyFrom(onetimekey.Key.ToArray())
                });
            }

            var resp = new GetPreKeyBundleResponse
            {
                Version = 1,
                PreKeyBundle = bundleDto
            };

            var responseEnvelope = new InternalEnvelope { GetPreKeyBundleResponse = resp };
            var responsePlain = Plaintext.FromBytesOwned(responseEnvelope.ToByteArray());
            var responseCipher = matched.Encrypt(responsePlain, clock);

            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                model.SessionsMutable[matchedSessionId] = matched;
            }
            finally
            {
                _stateGate.Release();
            }

            return new DeliverOpaqueMessageResponse
            {
                Version = 1,
                ResponsePayload = new DeliverOpaqueMessageResponse.Types.Payload
                {
                    Version = 1,
                    ResponsePayload = ByteString.CopyFrom(responseCipher.ToArray())
                }
            };
        }

        if (env.ApplicationPayloadCase == InternalEnvelope.ApplicationPayloadOneofCase.MessageQueueEnvelope
            && env.MessageQueueEnvelope?.MessageCase == MessageQueueEnvelope.MessageOneofCase.EnqueueOpaqueMessageRequest)
        {
            var enqueue = env.MessageQueueEnvelope.EnqueueOpaqueMessageRequest;
            if (!enqueue.HasRecipientPublicKeyHash || enqueue.RecipientPublicKeyHash.Length == 0)
                throw new InvalidOperationException("EnqueueOpaqueMessageRequest missing recipient_public_key_hash");
            if (!enqueue.HasMessageBlob || enqueue.MessageBlob.Length == 0)
                throw new InvalidOperationException("EnqueueOpaqueMessageRequest missing message_blob");

            await EnqueueRelayDownstreamToPeerAsync(
                relayHostNetworkPeerId: simulatedNetworkPeerId,
                targetIdentityPublicKeyHash: Percolator.Identity.IdentityPublicKeyHash.FromBytesOwned(enqueue.RecipientPublicKeyHash.ToByteArray()),
                opaqueBytes: enqueue.MessageBlob.ToByteArray(),
                debugType: "Opaque",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new DeliverOpaqueMessageResponse { Version = 1 };
        }

        return new DeliverOpaqueMessageResponse { Version = 1 };
    }

    public async Task PublishStandardPreKeyBundleToRelayAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId,
        DateTimeOffset expiresUtc,
        int oneTimeKeyCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Percolator.Identity.IdentityPublicKeyHash recipientPublicKeyHash;
        byte[] identityKey;
        Guid signedPreKeyId;
        byte[] signedPreKey;
        byte[] preKeySignature;
        IReadOnlyCollection<Percolator.Cryptography.OneTimeKeyInstance> oneTimeKeys;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var model = _peers.FirstOrDefault(p => p.NetworkPeerId == simulatedNetworkPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedNetworkPeerId}");

            var bundle = _engine.CreateStandardPreKeyBundle(
                peer: model,
                expiresUtc: expiresUtc,
                oneTimeKeyCount: oneTimeKeyCount);

            recipientPublicKeyHash = Percolator.Identity.IdentityPublicKeyHash.FromSpki(model.IdentitySigningKeySpki);
            identityKey = bundle.IdentitySigningKey.ToArray();
            signedPreKeyId = bundle.SignedPreKeyId;
            signedPreKey = bundle.SignedPreKey.ToArray();
            preKeySignature = bundle.SignedPreKeySignature.ToArray();
            oneTimeKeys = bundle.OneTimeKeys;
        }
        finally
        {
            _stateGate.Release();
        }

        await PublishPreKeyBundleAsync(
                relayHostNetworkPeerId: relayHostNetworkPeerId,
                recipientPublicKeyHash: recipientPublicKeyHash,
                logicalOwnerNetworkPeerId: simulatedNetworkPeerId,
                identityKey: identityKey,
                signedPreKeyId: signedPreKeyId,
                signedPreKey: signedPreKey,
                preKeySignature: preKeySignature,
                oneTimeKeys: oneTimeKeys,
                expiresUtc: expiresUtc,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.PreKeyBundleFetched,
            $"Pre-key bundle published -> relay={relayHostNetworkPeerId.ToString()[..8]} owner={simulatedNetworkPeerId.ToString()[..8]}",
            peerId: simulatedNetworkPeerId,
            relayHostPeerId: relayHostNetworkPeerId);
    }

    public async Task<SessionId?> InitiateStandardHandshakeToMainByRelayPkhAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId,
        Percolator.Identity.IdentityPublicKeyHash responderPublicKeyHash,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var popped = await TryPopPreKeyBundleByRecipientPkhAsync(relayHostNetworkPeerId, responderPublicKeyHash, cancellationToken)
            .ConfigureAwait(false);

        if (popped is null)
        {
            return null;
        }

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.PreKeyBundleFetched,
            $"Pre-key bundle fetched <- relay={relayHostNetworkPeerId.ToString()[..8]} for={simulatedNetworkPeerId.ToString()[..8]}",
            peerId: simulatedNetworkPeerId,
            relayHostPeerId: relayHostNetworkPeerId);

        // Serialize bundle from individual components
        var bundleProto = new GetPreKeyBundleResponse.Types.PreKeyBundle
        {
            Version = 1,
            IdentityKey = ByteString.CopyFrom(popped.IdentityKey),
            SignedPreKeyId = ByteString.CopyFrom(popped.SignedPreKeyId.ToByteArray()),
            SignedPreKey = ByteString.CopyFrom(popped.SignedPreKey),
            PreKeySignature = ByteString.CopyFrom(popped.PreKeySignature)
        };

        foreach (var otk in popped.OneTimeKeys)
        {
            bundleProto.OneTimeKeys.Add(new GetPreKeyBundleResponse.Types.OneTimeKey
            {
                OneTimeKeyId = ByteString.CopyFrom(otk.Id.ToByteArray()),
                KeyBytes = ByteString.CopyFrom(otk.Key.ToArray())
            });
        }

        if (!bundleProto.HasIdentityKey || bundleProto.IdentityKey.Length == 0) return null;
        if (!bundleProto.HasSignedPreKeyId || bundleProto.SignedPreKeyId.Length == 0) return null;
        if (!bundleProto.HasSignedPreKey || bundleProto.SignedPreKey.Length == 0) return null;
        if (!bundleProto.HasPreKeySignature || bundleProto.PreKeySignature.Length == 0) return null;

        var actualPkh = Percolator.Identity.IdentityPublicKeyHash.FromSpki(bundleProto.IdentityKey.ToByteArray());
        if (!actualPkh.Equals(responderPublicKeyHash))
        {
            return null;
        }

        Guid signedPreKeyGuid;
        try
        {
            signedPreKeyGuid = new Guid(bundleProto.SignedPreKeyId.ToByteArray());
        }
        catch
        {
            return null;
        }

        if (bundleProto.OneTimeKeys.Count > OneTimeKeyRequestSanityLimit)
        {
            return null;
        }
        if (bundleProto.OneTimeKeys.Count >0 && (!bundleProto.OneTimeKeys.First().HasKeyBytes || bundleProto.OneTimeKeys.First().KeyBytes.Length == 0))
        {
            return null;
        }
        var oneTimePreKeyInstance = bundleProto.OneTimeKeys
                .Select(k => new OneTimeKeyInstance(new Guid(k.OneTimeKeyId.ToByteArray()), OneTimeKey.FromBytesOwned(k.KeyBytes.ToByteArray())))
                .FirstOrDefault();
        
        var responderBundle = new Percolator.Cryptography.PreKeyBundle(
            identitySigningKey: RatchetIdentityKey.FromBytesOwned(bundleProto.IdentityKey.ToByteArray()),
            signedPreKeyId: signedPreKeyGuid,
            signedPreKey: PreKey.FromBytesOwned(bundleProto.SignedPreKey.ToByteArray()),
            signedPreKeySignature: Percolator.Cryptography.Signature.FromBytesOwned(bundleProto.PreKeySignature.ToByteArray()),
            oneTimePreKeyId: oneTimePreKeyInstance?.Id,
            oneTimePreKey: oneTimePreKeyInstance?.Key,
            expirationDateUtc: null);

        byte[] helloBytes;
        SessionId sessionId;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var model = _peers.FirstOrDefault(p => p.NetworkPeerId == simulatedNetworkPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedNetworkPeerId}");

            var initiated = _engine.TryInitiateStandardHandshake(model, responderBundle);
            if (initiated is null)
            {
                return null;
            }

            sessionId = initiated.SessionId;
            model.SetPendingStandardHandshakeToMain(responderPublicKeyHash, initiated.SessionId.Value);

            var hello = new HandshakeInitiatorHello
            {
                Version = 1,
                InitiatorIdentityKeySpki = ByteString.CopyFrom(initiated.InitiatorIdentitySigningKeySpki),
                InitiatorEphemeralKeySpki = ByteString.CopyFrom(initiated.InitiatorEphemeralKeySpki),
                SignedPreKeyId = ByteString.CopyFrom(initiated.SignedPreKeyId.ToByteArray()),
                InitiatorPublicIdentityId = ByteString.CopyFrom(model.PublicIdentityId.Value.ToByteArray())
            };
            if (initiated.OneTimePreKeyId is not null)
            {
                hello.OneTimePreKeyId = ByteString.CopyFrom(initiated.OneTimePreKeyId.Value.ToByteArray());
            }

            helloBytes = hello.ToByteArray();
        }
        finally
        {
            _stateGate.Release();
        }

        await EnqueueRelayDownstreamToPeerAsync(
                relayHostNetworkPeerId: relayHostNetworkPeerId,
                targetIdentityPublicKeyHash: responderPublicKeyHash,
                opaqueBytes: helloBytes,
                debugType: nameof(HandshakeInitiatorHello),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.StandardHandshakeHelloEnqueued,
            $"Standard handshake hello enqueued -> relay={relayHostNetworkPeerId.ToString()[..8]}",
            peerId: simulatedNetworkPeerId,
            relayHostPeerId: relayHostNetworkPeerId);

        return sessionId;
    }

    private const int OneTimeKeyRequestSanityLimit = 100;

    public async Task UpsertPendingStandardSignalHelloAsync(
        Percolator.Network.NetworkPeerId recipientNetworkPeerId,
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId,
        HandshakeInitiatorHello hello,
        DateTimeOffset receivedUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (hello is null) throw new ArgumentNullException(nameof(hello));

        if (!hello.HasInitiatorIdentityKeySpki || hello.InitiatorIdentityKeySpki.Length == 0)
        {
            return;
        }
        if (!hello.HasInitiatorPublicIdentityId || hello.InitiatorPublicIdentityId.Length == 0)
        {
            return;
        }

        var initiatorPkh = SHA256.HashData(hello.InitiatorIdentityKeySpki.ToByteArray());
        var initiatorPkhHex = Convert.ToHexString(initiatorPkh).ToLowerInvariant();

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var model = _peers.FirstOrDefault(p => p.NetworkPeerId == recipientNetworkPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {recipientNetworkPeerId}");

            model.PendingInboundStandardSignalHellosMutable[initiatorPkhHex] = new SimulatedPendingStandardSignalHelloModel(
                RelayHostNetworkPeerId: relayHostNetworkPeerId,
                PublicIdentityId: hello.HasInitiatorPublicIdentityId && hello.InitiatorPublicIdentityId.Length > 0
                    ? new PublicIdentityId(new Guid(hello.InitiatorPublicIdentityId.ToByteArray()))
                    : throw new InvalidOperationException("hello must have public identity id"),
                InitiatorIdentityKeySpki: hello.InitiatorIdentityKeySpki.ToByteArray(),
                InitiatorEphemeralKeySpki: hello.InitiatorEphemeralKeySpki.ToByteArray(),
                SignedPreKeyId: hello.HasSignedPreKeyId && hello.SignedPreKeyId.Length > 0
                    ? new Guid(hello.SignedPreKeyId.ToByteArray())
                    : Guid.Empty,
                OneTimePreKeyId: hello.HasOneTimePreKeyId && hello.OneTimePreKeyId.Length > 0
                    ? new Guid(hello.OneTimePreKeyId.ToByteArray())
                    : null,
                ReceivedUtc: receivedUtc);

            model.MarkAwaitingUserAcceptance();

            // Standard-signal pending UI takes precedence for the peer card. Clear reverse-signal correlation.
            // (User can still accept reverse-signal invites from the peer list tab.)
            // NOTE: this is a runtime-only UI convenience; it does not reject the invite.
            // The invite acceptance logic still keys on correlation id presence.
            //
            // If this behavior is undesired, remove it and allow both to be pending concurrently.
            //
            // For now: clear to match the plan's deterministic routing.
            //
            // B6.1: Single-slot semantics removed; no longer clearing InboundReverseSignalPendingCorrelationId
        }
        finally
        {
            _stateGate.Release();
        }

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.HandshakeStateTransition,
            $"Standard hello pending: initiator={initiatorPkhHex[..8]}",
            peerId: recipientNetworkPeerId,
            relayHostPeerId: relayHostNetworkPeerId,
            contextTag: "StandardSignalPending");
    }

    public async Task<bool> TryAcceptPendingStandardSignalHelloAsync(
        Percolator.Network.NetworkPeerId recipientNetworkPeerId,
        string initiatorPkhHex,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(initiatorPkhHex)) throw new ArgumentNullException(nameof(initiatorPkhHex));

        SimulatedPendingStandardSignalHelloModel? pending;
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var model = _peers.FirstOrDefault(p => p.NetworkPeerId == recipientNetworkPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {recipientNetworkPeerId}");

            if (!model.PendingInboundStandardSignalHellosMutable.TryGetValue(initiatorPkhHex, out pending))
            {
                return false;
            }

            relayHostNetworkPeerId = pending.RelayHostNetworkPeerId;
            model.PendingInboundStandardSignalHellosMutable.Remove(initiatorPkhHex);

            // B6.1: Check pending collections instead of single-slot correlation id
            var hasAnyPending = model.PendingInboundDirectInvites.Count > 0
                || model.PendingInboundStandardSignalHellosMutable.Count > 0;

            if (hasAnyPending)
            {
                model.MarkAwaitingUserAcceptance();
            }
            else
            {
                model.MarkEstablished();
            }
        }
        finally
        {
            _stateGate.Release();
        }

        EstablishSessionResponse response;
        try
        {
            var req = new EstablishSessionRequest
            {
                Version = 1,
                IdentitySigningKey = ByteString.CopyFrom(pending!.InitiatorIdentityKeySpki),
                EphemeralKey = ByteString.CopyFrom(pending.InitiatorEphemeralKeySpki),
                PrekeyId = ByteString.CopyFrom(pending.SignedPreKeyId.ToByteArray()),
                PublicIdentityId = ByteString.CopyFrom(pending.PublicIdentityId.Value.ToByteArray())
            };

            if (pending.OneTimePreKeyId.HasValue)
            {
                req.OnetimePrekeyId = ByteString.CopyFrom(pending.OneTimePreKeyId.Value.ToByteArray());
            }

            response = await ReceiveEstablishSessionFromMainAsync(recipientNetworkPeerId, req, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _diagnostics.Emit(
                SimulatorDiagnosticEventType.HandshakeStateTransition,
                $"Standard hello accept failed: {ex.Message}",
                peerId: recipientNetworkPeerId,
                relayHostPeerId: relayHostNetworkPeerId,
                contextTag: "StandardSignalAcceptFailed");
            throw;
        }

        // Set connection mode for relayed handshakes
        if (relayHostNetworkPeerId.Value != 0)
        {
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var model = _peers.FirstOrDefault(p => p.NetworkPeerId == recipientNetworkPeerId);
                if (model is not null)
                {
                    // Set connection mode to ViaRelay with the relay host peer ID
                    // Preserve existing Endpoint value
                    model.SetConnection(
                        ConnectionMode.ViaRelay,
                        endpoint: model.Endpoint.CurrentValue,
                        relayNetworkPeerId: relayHostNetworkPeerId);
                }
            }
            finally
            {
                _stateGate.Release();
            }
        }

        var initiatorPkh = Convert.FromHexString(initiatorPkhHex);
        var initiatorPkhTyped = Percolator.Identity.IdentityPublicKeyHash.FromBytes(initiatorPkh);
        var initiatorPeerId = await TryGetPeerIdByIdentityPublicKeyHashAsync(initiatorPkhTyped, cancellationToken).ConfigureAwait(false);

        if (initiatorPeerId is not null)
        {
            await EnqueueRelayDownstreamToPeerAsync(
                    relayHostNetworkPeerId: relayHostNetworkPeerId,
                    targetIdentityPublicKeyHash: initiatorPkhTyped,
                    opaqueBytes: response.ToByteArray(),
                    debugType: nameof(EstablishSessionResponse),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await EnqueueRelayUpstreamToMainAsync(
                    relayHostNetworkPeerId: relayHostNetworkPeerId,
                    opaqueBytes: response.ToByteArray(),
                    debugType: nameof(EstablishSessionResponse),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.HandshakeStateTransition,
            $"Standard hello accepted: initiator={initiatorPkhHex[..8]}",
            peerId: recipientNetworkPeerId,
            relayHostPeerId: relayHostNetworkPeerId,
            contextTag: "StandardSignalAccepted");

        return true;
    }

    private Task<EstablishSessionResponse> DeliverEstablishSessionToMainAsync(
        EstablishSessionRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        return _toMain.SendEstablishSessionToMainAsync(request, cancellationToken);
    }

    public async Task<byte[]> ComputePublicKeyHashAsync(Percolator.Network.NetworkPeerId simulatedNetworkPeerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var model = _peers.FirstOrDefault(p => p.NetworkPeerId == simulatedNetworkPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedNetworkPeerId}");
            return SHA256.HashData(model.IdentitySigningKeySpki);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task<SessionRatchetMessage> EncryptInternalEnvelopeAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        SessionId sessionId,
        InternalEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return EncryptInternalEnvelopeAsyncCore(simulatedNetworkPeerId, sessionId, envelope);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private SessionRatchetMessage EncryptInternalEnvelopeAsyncCore(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        SessionId sessionId,
        InternalEnvelope envelope)
    {
        var model = _peers.FirstOrDefault(p => p.NetworkPeerId == simulatedNetworkPeerId)
            ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedNetworkPeerId}");

        var plaintext = Plaintext.FromBytesOwned(envelope.ToByteArray());
        var cipher = _engine.Encrypt(model, sessionId, plaintext);
        return cipher;
    }

    public async Task<Plaintext> DecryptSessionMessageAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        SessionId sessionId,
        SessionRatchetMessage message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message is null) throw new ArgumentNullException(nameof(message));

        try
        {
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var model = _peers.FirstOrDefault(p => p.NetworkPeerId == simulatedNetworkPeerId)
                    ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedNetworkPeerId}");

                var pt = _engine.Decrypt(model, sessionId, message);
                return pt;
            }
            finally
            {
                _stateGate.Release();
            }
        }
        catch (Exception ex)
        {
            _diagnostics.Emit(
                SimulatorDiagnosticEventType.DecryptFailure,
                $"Decrypt failure: {ex.GetType().Name}: {ex.Message}",
                peerId: simulatedNetworkPeerId);
            throw;
        }
    }

    private SimulatedRelayModel GetRelayOrThrow(Percolator.Network.NetworkPeerId relayHostNetworkPeerId)
    {
        if (_relayByHostPeerId.TryGetValue(relayHostNetworkPeerId, out var relay)) return relay;
        throw new InvalidOperationException($"No relay exists with host peer id {relayHostNetworkPeerId}");
    }

    public async Task<bool> DeleteRelayMessageByAckIdAsync(Percolator.Network.NetworkPeerId relayHostNetworkPeerId, Guid ackId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool removed;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = GetRelayOrThrow(relayHostNetworkPeerId);
            removed = relay.RemoveMessage(ackId);
        }
        finally
        {
            _stateGate.Release();
        }

        if (removed)
        {
            _saveTrigger.OnNext(Unit.Default);
        }

        return removed;
    }

    public async Task<bool> MoveRelayMessageByAckIdAsync(Percolator.Network.NetworkPeerId relayHostNetworkPeerId, Guid ackId, int delta, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (delta == 0) return false;

        var changed = false;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = GetRelayOrThrow(relayHostNetworkPeerId);
            var ordered = relay.MessageQueue
                .Select(kvp => kvp.Value)
                .Select(x => (AckId: x.AckId, EnqueuedUtc: x.EnqueuedUtc))
                .OrderBy(x => x.EnqueuedUtc)
                .ToList();

            var idx = ordered.FindIndex(x => x.AckId == ackId);
            if (idx < 0) return false;

            var newIdx = idx + delta;
            if (newIdx < 0 || newIdx >= ordered.Count) return false;

            var before = newIdx > 0 ? ordered[newIdx - 1].EnqueuedUtc : (DateTimeOffset?)null;
            var after = newIdx < ordered.Count - 1 ? ordered[newIdx + 1].EnqueuedUtc : (DateTimeOffset?)null;

            DateTimeOffset newTime;
            if (before is null && after is null)
            {
                newTime = _timeProvider.GetUtcNow();
            }
            else if (before is null)
            {
                newTime = after.Value.AddTicks(-1);
            }
            else if (after is null)
            {
                newTime = before.Value.AddTicks(1);
            }
            else
            {
                var midTicks = (before.Value.UtcTicks + after.Value.UtcTicks) / 2;
                newTime = new DateTimeOffset(midTicks, TimeSpan.Zero);
                if (newTime <= before.Value) newTime = before.Value.AddTicks(1);
                if (newTime >= after.Value) newTime = after.Value.AddTicks(-1);
            }

            if (relay.MessageQueue.TryGetValue(ackId, out var msg))
            {
                relay.EnqueueMessage(msg switch
                {
                    OutboundRelayMessage outMsg => outMsg with { EnqueuedUtc = newTime },
                    InboundRelayMessage inMsg => inMsg with { EnqueuedUtc = newTime },
                    _ => msg
                });
                changed = true;
            }
            else
            {
                return false;
            }

            _diagnostics.Emit(
                SimulatorDiagnosticEventType.RelayReordered,
                $"Relay reorder: delta={delta}",
                relayHostPeerId: relayHostNetworkPeerId,
                ackId: ackId);

            changed = true;
        }
        finally
        {
            _stateGate.Release();
        }

        if (changed)
        {
            _saveTrigger.OnNext(Unit.Default);
        }

        return changed;
    }

    public async Task<bool> CorruptRelayMessageByAckIdAsync(Percolator.Network.NetworkPeerId relayHostNetworkPeerId, Guid ackId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var changed = false;
        var result = false;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = GetRelayOrThrow(relayHostNetworkPeerId);

            if (relay.MessageQueue.TryGetValue(ackId, out var msg))
            {
                switch (msg)
                {
                    case OutboundRelayMessage outMsg:
                    {
                        if (outMsg.OpaqueBytes is null || outMsg.OpaqueBytes.Length == 0) return false;
                        var bytes = outMsg.OpaqueBytes.ToArray();
                        bytes[0] = (byte)(bytes[0] ^ 0x01);
                        relay.EnqueueMessage(outMsg with { OpaqueBytes = bytes });
                        changed = true;
                        break;
                    }
                    case InboundRelayMessage inMsg:
                    {
                        if (inMsg.OpaqueBytes is null || inMsg.OpaqueBytes.Length == 0) return false;
                        var bytes = inMsg.OpaqueBytes.ToArray();
                        bytes[0] = (byte)(bytes[0] ^ 0x01);
                        relay.EnqueueMessage(inMsg with { OpaqueBytes = bytes });
                        changed = true;
                        break;
                    }
                }
            }
            else
            {
                return false;
            }

            _diagnostics.Emit(
                SimulatorDiagnosticEventType.RelayCorrupted,
                "Relay corrupt",
                relayHostPeerId: relayHostNetworkPeerId,
                ackId: ackId);

            result = true;
        }
        finally
        {
            _stateGate.Release();
        }

        if (changed)
        {
            _saveTrigger.OnNext(Unit.Default);
        }

        return result;
    }

    public async Task EnqueueRelayUpstreamToMainAsync(
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId,
        byte[] opaqueBytes,
        string? debugType = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (opaqueBytes is null) throw new ArgumentNullException(nameof(opaqueBytes));
        if (opaqueBytes.Length == 0) return;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = GetRelayOrThrow(relayHostNetworkPeerId);

            var msg = new OutboundRelayMessage(
                AckId: Guid.NewGuid(),
                OpaqueBytes: opaqueBytes,
                EnqueuedUtc: _timeProvider.GetUtcNow(),
                DebugType: debugType);

            relay.EnqueueMessage(msg);

            _diagnostics.Emit(
                SimulatorDiagnosticEventType.RelayEnqueued,
                $"Relay enqueue: {(debugType ?? "opaque")}",
                relayHostPeerId: relayHostNetworkPeerId,
                ackId: msg.AckId);
        }
        finally
        {
            _stateGate.Release();
        }

        _saveTrigger.OnNext(Unit.Default);
    }

    public async Task EnqueueRelayDownstreamToPeerAsync(
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId,
        Percolator.Identity.IdentityPublicKeyHash targetIdentityPublicKeyHash,
        byte[] opaqueBytes,
        string? debugType = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (opaqueBytes is null) throw new ArgumentNullException(nameof(opaqueBytes));
        if (opaqueBytes.Length == 0) return;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = GetRelayOrThrow(relayHostNetworkPeerId);

            var msg = new InboundRelayMessage(
                AckId: Guid.NewGuid(),
                TargetPkh: targetIdentityPublicKeyHash,
                OpaqueBytes: opaqueBytes,
                EnqueuedUtc: _timeProvider.GetUtcNow(),
                DebugType: debugType);

            relay.EnqueueMessage(msg);

            _diagnostics.Emit(
                SimulatorDiagnosticEventType.RelayEnqueued,
                $"Relay enqueue: {(debugType ?? "opaque")}",
                relayHostPeerId: relayHostNetworkPeerId,
                ackId: msg.AckId);
        }
        finally
        {
            _stateGate.Release();
        }

        _saveTrigger.OnNext(Unit.Default);
    }

    public async Task<IReadOnlyList<InboundRelayMessage>> DequeueRelayDownstreamToPeerAsync(
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId,
        Percolator.Identity.IdentityPublicKeyHash targetIdentityPublicKeyHash,
        int max,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (max <= 0) return Array.Empty<InboundRelayMessage>();

        List<InboundRelayMessage> snapshot;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = GetRelayOrThrow(relayHostNetworkPeerId);
            snapshot = relay.MessageQueue
                .Select(kvp => kvp.Value)
                .OfType<InboundRelayMessage>()
                .Where(x => x.TargetPkh == targetIdentityPublicKeyHash)
                .OrderBy(x => x.EnqueuedUtc)
                .Take(max)
                .ToList();
            if (snapshot.Count == 0)
            {
                return Array.Empty<InboundRelayMessage>();
            }

            foreach (var msg in snapshot)
            {
                relay.RemoveMessage(msg.AckId);
            }
        }
        finally
        {
            _stateGate.Release();
        }

        _saveTrigger.OnNext(Unit.Default);
        return snapshot;
    }

    public async Task<int> ForwardRelayUpstreamToMainAsync(
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId,
        SessionId relayHostToMainSessionId,
        int max,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (max <= 0) return 0;

        var forwarded = 0;
        while (forwarded < max)
        {
            cancellationToken.ThrowIfCancellationRequested();

            OutboundRelayMessage? next;

            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var relay = GetRelayOrThrow(relayHostNetworkPeerId);
                next = relay.MessageQueue
                    .Select(kvp => kvp.Value)
                    .OfType<OutboundRelayMessage>()
                    .OrderBy(x => x.EnqueuedUtc)
                    .FirstOrDefault();

                if (next is not null)
                {
                    // Checkout: remove before releasing gate so we can't double-send.
                    relay.RemoveMessage(next.AckId);
                }
            }
            finally
            {
                _stateGate.Release();
            }

            if (next is null)
            {
                break;
            }

            try
            {
                var env = new InternalEnvelope
                {
                    RelayOpaqueEnvelope = new RelayOpaqueEnvelope
                    {
                        Version = 1,
                        OpaquePayload = ByteString.CopyFrom(next.OpaqueBytes),
                        MessageAckId = ByteString.CopyFrom(next.AckId.ToByteArray())
                    }
                };

                var cipher = await EncryptInternalEnvelopeAsync(relayHostNetworkPeerId, relayHostToMainSessionId, env, cancellationToken)
                    .ConfigureAwait(false);

                var request = new DeliverOpaqueMessageRequest
                {
                    Version = 1,
                    Payload = ByteString.CopyFrom(cipher.ToArray())
                };

                var resp = await _toMain.SendOpaqueMessageToMainAsync(request, cancellationToken).ConfigureAwait(false);

                if (resp.ResultCase != DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload
                    || resp.ResponsePayload is null
                    || !resp.ResponsePayload.HasResponsePayload
                    || resp.ResponsePayload.ResponsePayload.Length == 0)
                {
                    await EnqueueRelayUpstreamToMainAsync(relayHostNetworkPeerId, next.OpaqueBytes, next.DebugType, cancellationToken).ConfigureAwait(false);
                    break;
                }

                var ackCipher = SessionRatchetMessage.FromBytesOwned(resp.ResponsePayload.ResponsePayload.ToByteArray());
                var ackPlain = await DecryptSessionMessageAsync(relayHostNetworkPeerId, relayHostToMainSessionId, ackCipher, cancellationToken)
                    .ConfigureAwait(false);
                var ack = RelayOpaqueResponse.Parser.ParseFrom(ackPlain.ToArray());
                if (!ack.HasMessageAckId || ack.MessageAckId.Length == 0)
                {
                    await EnqueueRelayUpstreamToMainAsync(relayHostNetworkPeerId, next.OpaqueBytes, next.DebugType, cancellationToken).ConfigureAwait(false);
                    break;
                }

                var returned = new Guid(ack.MessageAckId.ToByteArray());
                if (returned != next.AckId)
                {
                    await EnqueueRelayUpstreamToMainAsync(relayHostNetworkPeerId, next.OpaqueBytes, next.DebugType, cancellationToken).ConfigureAwait(false);
                    break;
                }

                forwarded++;
            }
            catch
            {
                await EnqueueRelayUpstreamToMainAsync(relayHostNetworkPeerId, next.OpaqueBytes, next.DebugType, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        _saveTrigger.OnNext(Unit.Default);

        return forwarded;
    }

    public async Task<bool> DeliverRelayUpstreamToMainByAckIdAsync(
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId,
        SessionId relayHostToMainSessionId,
        Guid ackId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        OutboundRelayMessage? msg;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = GetRelayOrThrow(relayHostNetworkPeerId);
            if (!relay.MessageQueue.TryGetValue(ackId, out var found) || found is not OutboundRelayMessage foundOut)
            {
                return false;
            }
            msg = foundOut;

            // Checkout: remove before releasing gate so we can't double-send.
            relay.RemoveMessage(msg.AckId);
        }
        finally
        {
            _stateGate.Release();
        }

        try
        {
            var env = new InternalEnvelope
            {
                RelayOpaqueEnvelope = new RelayOpaqueEnvelope
                {
                    Version = 1,
                    OpaquePayload = ByteString.CopyFrom(msg.OpaqueBytes),
                    MessageAckId = ByteString.CopyFrom(msg.AckId.ToByteArray())
                }
            };

            var cipher = await EncryptInternalEnvelopeAsync(relayHostNetworkPeerId, relayHostToMainSessionId, env, cancellationToken)
                .ConfigureAwait(false);

            var request = new DeliverOpaqueMessageRequest
            {
                Version = 1,
                Payload = ByteString.CopyFrom(cipher.ToArray())
            };

            var resp = await _toMain.SendOpaqueMessageToMainAsync(request, cancellationToken).ConfigureAwait(false);
            if (resp.ResultCase != DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload
                || resp.ResponsePayload is null
                || !resp.ResponsePayload.HasResponsePayload
                || resp.ResponsePayload.ResponsePayload.Length == 0)
            {
                await EnqueueRelayUpstreamToMainAsync(relayHostNetworkPeerId, msg.OpaqueBytes, msg.DebugType, cancellationToken).ConfigureAwait(false);
                return false;
            }

            var ackCipher = SessionRatchetMessage.FromBytesOwned(resp.ResponsePayload.ResponsePayload.ToByteArray());
            var ackPlain = await DecryptSessionMessageAsync(relayHostNetworkPeerId, relayHostToMainSessionId, ackCipher, cancellationToken)
                .ConfigureAwait(false);
            var ack = RelayOpaqueResponse.Parser.ParseFrom(ackPlain.ToArray());
            if (!ack.HasMessageAckId || ack.MessageAckId.Length == 0)
            {
                await EnqueueRelayUpstreamToMainAsync(relayHostNetworkPeerId, msg.OpaqueBytes, msg.DebugType, cancellationToken).ConfigureAwait(false);
                return false;
            }

            var returned = new Guid(ack.MessageAckId.ToByteArray());
            if (returned != msg.AckId)
            {
                await EnqueueRelayUpstreamToMainAsync(relayHostNetworkPeerId, msg.OpaqueBytes, msg.DebugType, cancellationToken).ConfigureAwait(false);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            await EnqueueRelayUpstreamToMainAsync(relayHostNetworkPeerId, msg.OpaqueBytes, msg.DebugType, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<EstablishSessionResponse?> ReceiveRelayedOpaquePayloadAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        byte[] opaqueBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (opaqueBytes is null) throw new ArgumentNullException(nameof(opaqueBytes));
        if (opaqueBytes.Length == 0) return null;

        // Standard handshake hellos are handled by the relay delivery service via UpsertPendingStandardSignalHelloAsync.

        try
        {
            var resp = EstablishSessionResponse.Parser.ParseFrom(opaqueBytes);
            if (resp is not null
                && resp.Response is not null
                && resp.Response.HasIdentitySigningKey && resp.Response.IdentitySigningKey.Length > 0
                && resp.Response.HasResponsePayload && resp.Response.ResponsePayload.Length > 0)
            {
                EstablishSessionResponse.Types.Response.Types.ResponsePayload payload;
                try
                {
                    payload = EstablishSessionResponse.Types.Response.Types.ResponsePayload.Parser.ParseFrom(resp.Response.ResponsePayload);
                }
                catch
                {
                    return null;
                }

                if (!payload.HasSessionId || string.IsNullOrWhiteSpace(payload.SessionId)) return null;

                Guid assignedGuid;
                try
                {
                    assignedGuid = Guid.Parse(payload.SessionId);
                }
                catch
                {
                    return null;
                }

                var responderPkh = Percolator.Identity.IdentityPublicKeyHash.FromSpki(resp.Response.IdentitySigningKey.ToByteArray());
                await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var model = _peers.FirstOrDefault(p => p.NetworkPeerId == simulatedNetworkPeerId)
                        ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedNetworkPeerId}");

                    var pendingPkh = model.PendingStandardHandshakeToMainResponderPublicKeyHash.CurrentValue;
                    var pendingSidGuid = model.PendingStandardHandshakeToMainTemporarySessionId.CurrentValue;
                    if (pendingPkh is null || pendingSidGuid is null)
                    {
                        return null;
                    }
                    if (!pendingPkh.Equals(responderPkh))
                    {
                        return null;
                    }

                    var pendingSessionId = new SessionId(pendingSidGuid.Value);

                    if (!model.SessionsMutable.TryGetValue(pendingSessionId, out var pendingSession))
                    {
                        return null;
                    }

                    var clock = ResolveClock();
                    var final = SecureSession.Create(
                        new SessionId(assignedGuid),
                        pendingSession.RemotePeerId,
                        pendingSession.ProtocolVersion,
                        pendingSession.State,
                        new AeadSessionCrypto(),
                        clock);

                    model.SessionsMutable.Remove(pendingSessionId);
                    model.SessionsMutable[final.Id] = final;

                    // Check if this was a relay handshake by looking at RelayHostPeerId
                    var relayHostPeerId = model.RelayHostPeerId.CurrentValue;
                    if (relayHostPeerId is not null && relayHostPeerId.Value.Value != 0u)
                    {
                        // Set connection mode to ViaRelay with the relay host peer ID
                        // Preserve existing Endpoint value
                        model.SetConnection(
                            ConnectionMode.ViaRelay,
                            endpoint: model.Endpoint.CurrentValue,
                            relayNetworkPeerId: new Percolator.Network.NetworkPeerId(relayHostPeerId.Value.Value));
                    }

                    model.ClearPendingStandardHandshakeToMain();
                }
                finally
                {
                    _stateGate.Release();
                }

                _diagnostics.Emit(
                    SimulatorDiagnosticEventType.HandshakeStateTransition,
                    $"Standard handshake established: sid={assignedGuid.ToString()[..8]}",
                    peerId: simulatedNetworkPeerId,
                    contextTag: "Established");

                return null;
            }
        }
        catch
        {
            // ignore
        }

        // Try to decrypt as a ratchet message (chat or other encrypted payload from Main via relay)
        try
        {
            var cipher = SessionRatchetMessage.FromBytes(opaqueBytes);
            var clock = ResolveClock();

            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var peerModel = _peers.FirstOrDefault(p => p.NetworkPeerId == simulatedNetworkPeerId);
                if (peerModel is null)
                {
                    return null;
                }

                foreach (var kv in peerModel.SessionsMutable)
                {
                    try
                    {
                        var pt = kv.Value.Decrypt(cipher, clock);
                        peerModel.SessionsMutable[kv.Key] = kv.Value;

                        if (pt.ToArray().Length == 0)
                        {
                            return null;
                        }

                        var innerEnv = InternalEnvelope.Parser.ParseFrom(pt.ToArray());
                        if (innerEnv.ApplicationPayloadCase == InternalEnvelope.ApplicationPayloadOneofCase.ChatEnvelope
                            && innerEnv.ChatEnvelope?.MessageCase == ChatEnvelope.MessageOneofCase.TextMessage)
                        {
                            var txt = innerEnv.ChatEnvelope.TextMessage;
                            peerModel.AddChatMessage(
                                isFromMain: true,
                                content: txt.Content,
                                receivedUtc: txt.SentTimestampUtc?.ToDateTimeOffset() ?? _timeProvider.GetUtcNow());
                            _saveTrigger.OnNext(Unit.Default);
                        }
                        return null; // Decrypted successfully, handled
                    }
                    catch { /* not this session */ }
                }
            }
            finally
            {
                _stateGate.Release();
            }
        }
        catch { /* not a ratchet message */ }

        return null;
    }

    public async Task SendChatMessageToMainAsync(Percolator.Network.NetworkPeerId simulatedNetworkPeerId, string content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        var chatEnvelope = new ChatEnvelope
        {
            TextMessage = new TextMessage
            {
                MessageId = Google.Protobuf.ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                Content = content,
                SentTimestampUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(now)
            }
        };
        var internalEnvelope = new InternalEnvelope { ChatEnvelope = chatEnvelope };

        Percolator.Network.NetworkPeerId? relayHostPeerId = null;
        byte[]? cipherBytes = null;
        bool useRelay = false;
        bool useDirect = false;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var model = _peers.FirstOrDefault(p => p.NetworkPeerId == simulatedNetworkPeerId)
                ?? throw new InvalidOperationException($"No simulated peer with id {simulatedNetworkPeerId}");

            // Session selection: Select the most recently created session
            var sessionId = model.SessionsMutable
                .OrderByDescending(kv => kv.Value.CreatedAtUtc)
                .Select(kv => kv.Key)
                .FirstOrDefault();

            if (sessionId == default)
                throw new InvalidOperationException($"No session found for peer {simulatedNetworkPeerId} to send chat message");

            // Encrypt using the existing helper
            var cipher = EncryptInternalEnvelopeAsyncCore(simulatedNetworkPeerId, sessionId, internalEnvelope);

            // Record outbound message in peer's chat history
            model.AddChatMessage(isFromMain: false, content: content, receivedUtc: now);

            // Determine routing and capture necessary values while holding the gate
            if (model.ConnectionMode.CurrentValue == ConnectionMode.ViaRelay && model.RelayPeerId.CurrentValue.Value != 0u)
            {
                relayHostPeerId = model.RelayPeerId.CurrentValue;
                if (relayHostPeerId.Value.Value == 0u)
                {
                    throw new InvalidOperationException("Relay peer ID is empty, but connection mode is ViaRelay");
                }

                cipherBytes = cipher.ToArray();
                useRelay = true;
            }
            else
            {
                cipherBytes = cipher.ToArray();
                useDirect = true;
            }
        }
        finally
        {
            _stateGate.Release();
        }

        // Perform routing outside the state gate to avoid deadlock
        if (useRelay)
        {
            await EnqueueRelayUpstreamToMainAsync(
                relayHostNetworkPeerId: relayHostPeerId.Value,
                opaqueBytes: cipherBytes!,
                debugType: "Chat",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else if (useDirect)
        {
            var request = new DeliverOpaqueMessageRequest
            {
                Version = 1,
                Payload = Google.Protobuf.ByteString.CopyFrom(cipherBytes!)
            };

            _ = await _toMain.SendOpaqueMessageToMainAsync(request, cancellationToken).ConfigureAwait(false);
        }
        _saveTrigger.OnNext(Unit.Default);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Task? inFlight;
        lock (_initGate)
        {
            inFlight = _initializeTask;
            if (inFlight is null || inFlight.IsCompleted)
            {
                _initializeTask = InitializeCoreAsync(cancellationToken);
                inFlight = _initializeTask;
            }
        }

        await inFlight.ConfigureAwait(false);
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var d in _runtimePersistenceByPeerId.Values)
            {
                d.Dispose();
            }
            _runtimePersistenceByPeerId.Clear();

            foreach (var d in _relayPersistenceByHostPeerId.Values)
            {
                d.Dispose();
            }
            _relayPersistenceByHostPeerId.Clear();

            var snapshot = await _store.LoadStateAsync(cancellationToken).ConfigureAwait(false);

            await WithStateGateAsync(async () =>
            {
                _groups = snapshot.Groups?.ToList() ?? new();

                _peers.Clear();
                _peerById.Clear();

                foreach (var p in snapshot.Peers)
                {
                    var model = CreatePeerFromSnapshot(p);
                    _peers.Add(model);
                    _peerById[model.NetworkPeerId] = model;
                    AttachRuntimePersistence(model);
                    AttachEndpointIndex(model);
                }

                _relationships.Clear();
                foreach (var rel in snapshot.Relationships)
                {
                    _relationships.Add(new PeerRelationship(rel.SourceNetworkPeerId, rel.TargetNetworkPeerId, rel.Type));
                }

                _relays.Clear();
                _relayByHostPeerId.Clear();
                foreach (var relaySnap in snapshot.Relays)
                {
                    var relay = CreateRelayFromSnapshot(relaySnap);
                    _relays.Add(relay);
                    _relayByHostPeerId[relay.RelayHostNetworkPeerId] = relay;
                    AttachRelayPersistence(relay);
                }

                var max = snapshot.Peers.Count == 0 ? (99000 - 1) : snapshot.Peers.Max(x => x.SelfIdentityId);
                _nextSelfIdentityId = Math.Max(99000 - 1, max);

                return Task.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_initGate)
            {
                _initializeTask = null;
            }
        }
    }

    private SimulatedPeerModel CreatePeerFromSnapshot(PeerStateSnapshot snap)
    {
        var peer = new SimulatedPeerModel(
            networkPeerId: snap.NetworkPeerId,
            publicIdentityId: snap.PublicIdentityId,
            selfIdentityId: snap.SelfIdentityId,
            displayName: snap.DisplayName,
            isRelayCapable: snap.IsRelayCapable,
            identitySigningKeySpki: snap.IdentitySigningKeySpki,
            identitySigningKeyPrivateKeyEcPrivateKey: snap.IdentitySigningKeyPrivateKeyEcPrivateKey,
            connectionMode: snap.ConnectionMode,
            endpoint: snap.Endpoint,
            relayPeerId: snap.RelayNetworkPeerId.Value == 0 ? null : snap.RelayNetworkPeerId,
            targetPublicKeyHash: snap.TargetPublicKeyHash,
            selectedRouteMode: snap.SelectedRouteMode,
            directEndpoint: snap.DirectEndpoint,
            relayHostPeerId: snap.RelayHostPeerId,
            phase: snap.Phase,
            notUntilUtc: snap.NotUntilUtc,
            lastError: snap.LastError,
            handshakeAttempts: snap.HandshakeAttempts.ToList(),
            knownPeerIds: snap.KnownPeerIds.ToList(),
            publishedPreKeyBundles: snap.PublishedPreKeyBundles
                .Select(b => new SimulatedPublishedPreKeyBundleModel(
                    b.RecipientPublicKeyHash,
                    b.LogicalOwnerNetworkPeerId,
                    b.IdentityKey,
                    b.SignedPreKeyId,
                    b.SignedPreKey,
                    b.PreKeySignature,
                    new ObservableList<Percolator.Cryptography.OneTimeKeyInstance>(
                        b.OneTimeKeys.Select(otk => new Percolator.Cryptography.OneTimeKeyInstance(
                            otk.Id,
                            Percolator.Cryptography.OneTimeKey.FromBytes(otk.KeyBytes)))),
                    b.ExpiresUtc))
                .ToList());

        // Hydrate chat messages
        foreach (var msg in snap.RecentChatMessages)
        {
            peer.RecentChatMessagesMutable.Add(msg);
        }

        // Hydrate signed prekeys
        foreach (var spk in snap.SignedPreKeys)
        {
            peer.SignedPreKeysMutable.Add(new SimulatedSignedPreKeyModel(
                spk.SignedPreKeyId,
                spk.PrivateEcPrivateKey,
                spk.PublicSpki));
        }

        // Hydrate outbound invites
        foreach (var invite in snap.OutboundInvites)
        {
            peer.OutboundInvitesMutable.Add(new SimulatedOutboundInviteModel(
                invite.CorrelationId,
                invite.SignedPreKeyPrivateEcPrivateKey));
        }
        
        foreach (var invite in snap.PendingInboundDirectInvites)
        {
            peer.PendingInboundDirectInvitesMutable.Add(new SimulatedPendingInboundDirectInviteModel(
                invite.CorrelationId,
                invite.RequestBytes,
                invite.ReceivedAtUtc,
                invite.InviterIdentityKeySpki,
                invite.InviterNetworkPeerId));
        }

        // Hydrate sessions
        foreach (var sessionSnap in snap.Sessions)
        {
            var rootKey = RootKey.FromBytes(sessionSnap.RootKey);
            var sendChainKey = sessionSnap.SendChainKey is not null ? ChainKey.FromBytes(sessionSnap.SendChainKey) : null;
            var recvChainKey = sessionSnap.RecvChainKey is not null ? ChainKey.FromBytes(sessionSnap.RecvChainKey) : null;
            var remoteRatchetKey = sessionSnap.RemoteRatchetKey is not null ? RatchetEphemeralKey.FromBytes(sessionSnap.RemoteRatchetKey) : null;
            var dhRatchetPrivateKey = sessionSnap.DhRatchetPrivateKey is not null ? PrivateEphemeralKey.FromBytes(sessionSnap.DhRatchetPrivateKey) : null;

            var ratchetState = new RatchetState(
                rootKey: rootKey,
                sendingChainKey: sendChainKey,
                sendingCounter: sessionSnap.SendCounter,
                receivingChainKey: recvChainKey,
                receivingCounter: sessionSnap.RecvCounter,
                previousChainLength: sessionSnap.PrevChainLength,
                remoteRatchetKey: remoteRatchetKey,
                dhRatchetPrivateKey: dhRatchetPrivateKey,
                skippedKeyLimit: 1000);

            var clock = ResolveClock();
            var sessionCrypto = new AeadSessionCrypto();
            var session = SecureSession.Create(
                id: new SessionId(sessionSnap.SessionId),
                remotePeerId: new Percolator.Cryptography.Primitives.PeerId(sessionSnap.RemotePeerId.Value),
                protocolVersion: new ProtocolVersion(sessionSnap.ProtocolVersion),
                state: ratchetState,
                sessionCrypto: sessionCrypto,
                clock: clock);

            // Note: CreatedAtUtc and LastUsedAtUtc will be set to current time on restore.
            // The cryptographic state (ratchet keys, counters) is preserved, which is what matters for decryption.

            peer.SessionsMutable[session.Id] = session;
        }

        // Compute derived UiState after hydration (Chunk C Option A)
        peer.RecomputeDerivedUiState();

        return peer;
    }

    private static SimulatedRelayModel CreateRelayFromSnapshot(RelayStateSnapshot snap)
    {
        var relay = new SimulatedRelayModel(snap.RelayHostNetworkPeerId);
        foreach (var m in snap.UpstreamToMain)
        {
            if (m.AckId == Guid.Empty) continue;
            relay.EnqueueMessage(new OutboundRelayMessage(m.AckId, m.OpaqueBytes, m.EnqueuedUtc, m.DebugType));
        }
        foreach (var m in snap.DownstreamToPeers)
        {
            if (m.AckId == Guid.Empty) continue;
            if (m.TargetIdentityPublicKeyHash is null) continue;
            relay.EnqueueMessage(new InboundRelayMessage(m.AckId, m.TargetIdentityPublicKeyHash, m.OpaqueBytes, m.EnqueuedUtc, m.DebugType));
        }
        return relay;
    }

    public async Task<Percolator.Network.NetworkPeerId> AddPeerAsync(string? displayName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var peerId = new Percolator.Network.NetworkPeerId((uint)Random.Shared.Next());

        byte[] priv;
        byte[] spki;
        using (var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
        {
            priv = ecdh.ExportECPrivateKey();
            spki = ecdh.ExportSubjectPublicKeyInfo();
        }

        var name = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();

        SimulatedPeerModel model;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var endpoint = AllocateNextLoopbackEndpointOnPeerGate();

            var selfIdentityId = Interlocked.Increment(ref _nextSelfIdentityId);
            var publicIdentityId = PublicIdentityId.NewId();

            model = new SimulatedPeerModel(
                networkPeerId: peerId,
                publicIdentityId: publicIdentityId,
                selfIdentityId: selfIdentityId,
                displayName: name,
                isRelayCapable: false,
                identitySigningKeySpki: spki,
                identitySigningKeyPrivateKeyEcPrivateKey: priv,
                connectionMode: ConnectionMode.Direct,
                endpoint: endpoint,
                relayPeerId: null);

            _peerById[peerId] = model;
            _peers.Add(model);
            AttachRuntimePersistence(model);
            AttachEndpointIndex(model);
        }
        finally
        {
            _stateGate.Release();
        }

        _saveTrigger.OnNext(Unit.Default);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.PeerCreated,
            $"Peer created: {(string.IsNullOrWhiteSpace(model.DisplayName.CurrentValue) ? model.NetworkPeerId.ToString()[..8] : model.DisplayName.CurrentValue)}",
            peerId: peerId);

        return peerId;
    }

    private DnsEndPoint AllocateNextLoopbackEndpointOnPeerGate()
    {
        // Must be called under _stateGate.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _peers)
        {
            var endpoint = p.Endpoint.CurrentValue;
            if (endpoint is not null && !string.IsNullOrWhiteSpace(endpoint.Host))
            {
                used.Add(endpoint.Host);
            }
        }

        var port = _transportOptions.Value.SimulatorPort;
        if (port == 0) port = 5002;

        for (var x = 1; x <= 254; x++)
        {
            for (var y = 1; y <= 254; y++)
            {
                var candidate = $"127.77.{x}.{y}";
                if (!used.Contains(candidate))
                {
                    return new DnsEndPoint(candidate, port);
                }
            }
        }

        // Fallback: extremely unlikely. Preserve previous deterministic mapping.
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Guid.NewGuid().ToByteArray());
        var fx = (byte)((hash[0] % 254) + 1);
        var fy = (byte)((hash[1] % 254) + 1);
        return new DnsEndPoint($"127.77.{fx}.{fy}", port);
    }

    public async Task<Percolator.Network.NetworkPeerId?> TryGetPeerIdByIdentityPublicKeyHashAsync(Percolator.Identity.IdentityPublicKeyHash recipientPublicKeyHash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        List<Percolator.Network.NetworkPeerId> matches;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            matches = _peerById.Values
                .Where(p => Percolator.Identity.IdentityPublicKeyHash.FromSpki(p.IdentitySigningKeySpki).Equals(recipientPublicKeyHash))
                .Select(p => p.NetworkPeerId)
                .Take(2)
                .ToList();
        }
        finally
        {
            _stateGate.Release();
        }

        if (matches.Count != 1) return null;
        return matches[0];
    }

    public async Task RemovePeerAsync(Percolator.Network.NetworkPeerId networkPeerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string name;
        SimulatedPeerModel? removedModel = null;
        DnsEndPoint? removedEndpoint = null;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_peerById.Remove(networkPeerId, out removedModel)) return;
            name = string.IsNullOrWhiteSpace(removedModel.DisplayName.CurrentValue) ? removedModel.NetworkPeerId.ToString()[..8] : removedModel.DisplayName.CurrentValue;
            removedEndpoint = removedModel.Endpoint.CurrentValue;

            for (var i = _relationships.Count - 1; i >= 0; i--)
            {
                var rel = _relationships[i];
                if (rel.SourceNetworkPeerId == networkPeerId || rel.TargetNetworkPeerId == networkPeerId)
                {
                    _relationships.RemoveAt(i);
                }
            }
        }
        finally
        {
            _stateGate.Release();
        }

        if (removedModel is not null)
        {
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _ = _peers.Remove(removedModel);
            }
            finally
            {
                _stateGate.Release();
            }
        }

        // Remove from endpoint index
        _endpointIndex.TryRemove(removedEndpoint!, out _);

        await RemoveRelayIfExistsAsync(networkPeerId, cancellationToken).ConfigureAwait(false);

        removedModel?.Dispose();

        _saveTrigger.OnNext(Unit.Default);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.PeerRemoved,
            $"Peer removed: {name}",
            peerId: networkPeerId);
    }

    public async Task AddPublishedKeysRelationshipAsync(Percolator.Network.NetworkPeerId publisherNetworkPeerId, Percolator.Network.NetworkPeerId hostNetworkPeerId, CancellationToken cancellationToken = default)
    {
        if (publisherNetworkPeerId == hostNetworkPeerId) return;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_peerById.ContainsKey(publisherNetworkPeerId)) return;
            if (!_peerById.ContainsKey(hostNetworkPeerId)) return;

            var rel = new PeerRelationship(publisherNetworkPeerId, hostNetworkPeerId, RelationshipType.PublishedKey);
            if (_relationships.Contains(rel)) return;

            _relationships.Add(rel);
        }
        finally
        {
            _stateGate.Release();
        }

        _saveTrigger.OnNext(Unit.Default);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.PreKeyPublishRelationshipAdded,
            $"Pre-keys relationship added: {publisherNetworkPeerId.ToString()[..8]} -> {hostNetworkPeerId.ToString()[..8]}",
            peerId: publisherNetworkPeerId);
    }

    public async Task RemovePublishedKeysRelationshipAsync(Percolator.Network.NetworkPeerId publisherNetworkPeerId, Percolator.Network.NetworkPeerId hostNetworkPeerId, CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rel = new PeerRelationship(publisherNetworkPeerId, hostNetworkPeerId, RelationshipType.PublishedKey);
            if (!_relationships.Contains(rel)) return;

            _ = _relationships.Remove(rel);
        }
        finally
        {
            _stateGate.Release();
        }

        _saveTrigger.OnNext(Unit.Default);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.PreKeyPublishRelationshipRemoved,
            $"Pre-keys relationship removed: {publisherNetworkPeerId.ToString()[..8]} -> {hostNetworkPeerId.ToString()[..8]}",
            peerId: publisherNetworkPeerId);
    }

    public async Task AddRelayActiveSessionAsync(Percolator.Network.NetworkPeerId relayHostNetworkPeerId, Percolator.Network.NetworkPeerId networkPeerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (relayHostNetworkPeerId == networkPeerId) return;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_peerById.ContainsKey(relayHostNetworkPeerId)) return;
            if (!_peerById.ContainsKey(networkPeerId)) return;

            var rel = new PeerRelationship(relayHostNetworkPeerId, networkPeerId, RelationshipType.RelayActiveSession);
            if (_relationships.Contains(rel)) return;

            _relationships.Add(rel);
        }
        finally
        {
            _stateGate.Release();
        }

        _saveTrigger.OnNext(Unit.Default);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.RelayActiveSessionAdded,
            $"Relay active session added: relay={relayHostNetworkPeerId.ToString()[..8]} peer={networkPeerId.ToString()[..8]}",
            peerId: networkPeerId,
            relayHostPeerId: relayHostNetworkPeerId);
    }

    public async Task RemoveRelayActiveSessionAsync(Percolator.Network.NetworkPeerId relayHostNetworkPeerId, Percolator.Network.NetworkPeerId networkPeerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (relayHostNetworkPeerId == networkPeerId) return;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rel = new PeerRelationship(relayHostNetworkPeerId, networkPeerId, RelationshipType.RelayActiveSession);
            if (!_relationships.Contains(rel)) return;

            _ = _relationships.Remove(rel);
        }
        finally
        {
            _stateGate.Release();
        }

        _saveTrigger.OnNext(Unit.Default);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.RelayActiveSessionRemoved,
            $"Relay active session removed: relay={relayHostNetworkPeerId.ToString()[..8]} peer={networkPeerId.ToString()[..8]}",
            peerId: networkPeerId,
            relayHostPeerId: relayHostNetworkPeerId);
    }

    public async Task PublishPreKeyBundleAsync(
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId,
        Percolator.Identity.IdentityPublicKeyHash recipientPublicKeyHash,
        Percolator.Network.NetworkPeerId logicalOwnerNetworkPeerId,
        byte[] identityKey,
        Guid signedPreKeyId,
        byte[] signedPreKey,
        byte[] preKeySignature,
        IReadOnlyCollection<Percolator.Cryptography.OneTimeKeyInstance> oneTimeKeys,
        DateTimeOffset expiresUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_peerById.TryGetValue(relayHostNetworkPeerId, out var host)) return;

            host.PublishedPreKeyBundles.Add(new SimulatedPublishedPreKeyBundleModel(
                RecipientPublicKeyHash: recipientPublicKeyHash,
                LogicalOwnerNetworkPeerId: logicalOwnerNetworkPeerId,
                IdentityKey: identityKey,
                SignedPreKeyId: signedPreKeyId,
                SignedPreKey: signedPreKey,
                PreKeySignature: preKeySignature,
                OneTimeKeys: new ObservableList<Percolator.Cryptography.OneTimeKeyInstance>(oneTimeKeys),
                ExpiresUtc: expiresUtc));
        }
        finally
        {
            _stateGate.Release();
        }

        _saveTrigger.OnNext(Unit.Default);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.RelayEnqueued,
            "Pre-key bundle published",
            peerId: logicalOwnerNetworkPeerId,
            relayHostPeerId: relayHostNetworkPeerId,
            contextTag: recipientPublicKeyHash.ToString());
    }

    internal async Task<SimulatedPublishedPreKeyBundleModel?> TryPopPreKeyBundleByRecipientPkhAsync(
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId,
        Percolator.Identity.IdentityPublicKeyHash recipientPublicKeyHash,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_peerById.TryGetValue(relayHostNetworkPeerId, out var host)) return null;

            var now = _timeProvider.GetUtcNow();
            for (var i = host.PublishedPreKeyBundles.Count - 1; i >= 0; i--)
            {
                if (host.PublishedPreKeyBundles[i].ExpiresUtc <= now)
                {
                    host.PublishedPreKeyBundles.RemoveAt(i);
                }
            }

            var match = host.PublishedPreKeyBundles
                .FirstOrDefault(b => b.RecipientPublicKeyHash.Equals(recipientPublicKeyHash));

            if (match is null)
            {
                return null;
            }

            // Pop one onetime key if available
            Percolator.Cryptography.OneTimeKeyInstance? poppedOneTimeKey = null;
            if (match.OneTimeKeys.Count > 0)
            {
                poppedOneTimeKey = match.OneTimeKeys[0];
                match.OneTimeKeys.RemoveAt(0);
            }

            var responseOneTimeKeys = poppedOneTimeKey is null
                ? new ObservableList<Percolator.Cryptography.OneTimeKeyInstance>()
                : new ObservableList<Percolator.Cryptography.OneTimeKeyInstance>(new[] { poppedOneTimeKey });

            // Return a new bundle containing only the popped onetime key (if any)
            var result = new SimulatedPublishedPreKeyBundleModel(
                RecipientPublicKeyHash: match.RecipientPublicKeyHash,
                LogicalOwnerNetworkPeerId: match.LogicalOwnerNetworkPeerId,
                IdentityKey: match.IdentityKey,
                SignedPreKeyId: match.SignedPreKeyId,
                SignedPreKey: match.SignedPreKey,
                PreKeySignature: match.PreKeySignature,
                OneTimeKeys: responseOneTimeKeys,
                ExpiresUtc: match.ExpiresUtc);

            // Note: We don't remove the bundle entry - it stays with remaining onetime keys
            // It will be removed when it expires or when manually cleared

            _saveTrigger.OnNext(Unit.Default);
            return result;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private void AttachRuntimePersistence(SimulatedPeerModel model)
    {
        if (_runtimePersistenceByPeerId.ContainsKey(model.NetworkPeerId)) return;

        var tracker = new SimulatedPeerRuntimeTracker(model);

        var saveSub = tracker.Dirty.Subscribe(_ => _saveTrigger.OnNext(Unit.Default));

        var lifecycleSub = model.IsRelayCapable
            .Skip(1) // Prevent double-loading during InitializeCoreAsync
            .DistinctUntilChanged()
            .SubscribeAwait(async (enabled, ct) => await OnRelayCapabilityChangedAsync(model.NetworkPeerId, enabled, ct).ConfigureAwait(false), AwaitOperation.Sequential);

        _runtimePersistenceByPeerId[model.NetworkPeerId] = new CompositeDisposable(tracker, saveSub, lifecycleSub);
    }

    private void AttachEndpointIndex(SimulatedPeerModel model)
    {
        // Subscribe to endpoint changes and update the index
        var oldEndpoint = model.Endpoint.CurrentValue;
        var endpointSub = model.Endpoint
            .DistinctUntilChanged()
            .Subscribe(endpoint =>
            {
                // Remove old endpoint from index if it exists
                _endpointIndex.TryRemove(oldEndpoint, out _);
                _endpointIndex[endpoint] = model.NetworkPeerId;
                oldEndpoint = endpoint;
            });

        // Initialize the index with the current endpoint
        _endpointIndex[oldEndpoint] = model.NetworkPeerId;

        // Store the subscription to dispose it when the peer is removed
        // We'll attach this to the peer's lifecycle
        model.Track(endpointSub);
    }

    public bool TryResolvePeerId(DnsEndPoint endpoint, out Percolator.Network.NetworkPeerId? networkPeerId)
    {
        if (_endpointIndex.TryGetValue(endpoint, out var peerId))
        {
            networkPeerId = peerId;
            return true;
        }
        
        networkPeerId = null;
        return false;
    }

    private async Task OnRelayCapabilityChangedAsync(Percolator.Network.NetworkPeerId networkPeerId, bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!enabled)
        {
            await RemoveRelayIfExistsAsync(networkPeerId, cancellationToken).ConfigureAwait(false);
            return;
        }

        SimulatedRelayModel? addedRelay = null;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_relayByHostPeerId.ContainsKey(networkPeerId)) return;

            var relay = new SimulatedRelayModel(networkPeerId);
            _relayByHostPeerId[networkPeerId] = relay;
            _relays.Add(relay);
            AttachRelayPersistence(relay);
            addedRelay = relay;
        }
        finally
        {
            _stateGate.Release();
        }

        if (addedRelay is not null)
        {
            _saveTrigger.OnNext(Unit.Default);
        }
    }

    private void AttachRelayPersistence(SimulatedRelayModel relay)
    {
        if (_relayPersistenceByHostPeerId.ContainsKey(relay.RelayHostNetworkPeerId)) return;

        var tracker = new SimulatedRelayProtocolStateTracker(relay);
        var saveSub = tracker.Dirty.Subscribe(_ => _saveTrigger.OnNext(Unit.Default));
        _relayPersistenceByHostPeerId[relay.RelayHostNetworkPeerId] = new CompositeDisposable(tracker, saveSub);
    }

    private async Task RemoveRelayIfExistsAsync(Percolator.Network.NetworkPeerId relayHostNetworkPeerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SimulatedRelayModel? relay = null;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_relayByHostPeerId.TryGetValue(relayHostNetworkPeerId, out relay)) return;

            _relayByHostPeerId.Remove(relayHostNetworkPeerId);

            if (_relayPersistenceByHostPeerId.Remove(relayHostNetworkPeerId, out var d))
            {
                d.Dispose();
            }

            _ = _relays.Remove(relay);
        }
        finally
        {
            _stateGate.Release();
        }

        relay.Dispose();

        _saveTrigger.OnNext(Unit.Default);
    }

}
