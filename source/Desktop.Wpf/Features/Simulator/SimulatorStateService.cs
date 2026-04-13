using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ObservableCollections;
using Percolator.Application.Configuration;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using R3;
using Desktop.Wpf.Features.Simulator.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator.Tracking;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorStateService : ISimulatorStateService, ISimulatorStateInitializer
{
    private readonly ISimulatorStateRepository _store;
    private readonly ISimulatorDiagnosticsService _diagnostics;
    private readonly ISimulatedPeerPendingInbox _pending;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Desktop.Wpf.Features.Simulator.Protocol.ISignalProtocolEngine _engine;
    private readonly IOptions<TransportOptions> _transportOptions;

    private readonly Subject<Unit> _saveTrigger = new();
    private DisposableBag _bag;

    private readonly ObservableList<SimulatedPeerModel> _peers = new();
    public IReadOnlyObservableList<SimulatedPeerModel> Peers => _peers;

    private readonly ObservableList<SimulatedRelayModel> _relays = new();
    public IReadOnlyObservableList<SimulatedRelayModel> Relays => _relays;

    private readonly ObservableList<PeerRelationship> _relationships = new();
    public IReadOnlyObservableList<PeerRelationship> Relationships => _relationships;

    private readonly Dictionary<Guid, SimulatedRelayModel> _relayByHostPeerId = new();

    private readonly Dictionary<Guid, SimulatedPeerModel> _peerById = new();

    private readonly Dictionary<Guid, IDisposable> _runtimePersistenceByPeerId = new();

    private readonly Dictionary<Guid, IDisposable> _relayPersistenceByHostPeerId = new();

    private List<GroupConversationDto> _groups = new();

    private int _nextSelfIdentityId = 99000 - 1;

    private readonly object _initGate = new();
    private Task? _initializeTask;

    private readonly SemaphoreSlim _stateGate = new(1, 1);

    private readonly TimeProvider _timeProvider;

    public SimulatorStateService(
        ISimulatorStateRepository store,
        ISimulatorDiagnosticsService diagnostics,
        ISimulatedPeerPendingInbox pending,
        IServiceScopeFactory scopeFactory,
        IOptions<TransportOptions> transportOptions,
        Desktop.Wpf.Features.Simulator.Protocol.ISignalProtocolEngine engine,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _diagnostics = diagnostics;
        _pending = pending;
        _scopeFactory = scopeFactory;
        _transportOptions = transportOptions;
        _engine = engine;

        _timeProvider = timeProvider ?? ObservableSystem.DefaultTimeProvider;

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
                .Select(r => new PeerRelationshipSnapshot(r.SourcePeerId, r.TargetPeerId, r.Type))
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
        Guid simulatedPeerId,
        Guid inviterPeerId,
        EstablishDirectSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null) throw new ArgumentNullException(nameof(request));

        var acceptance = await AcceptReverseSignalInviteAsync(simulatedPeerId, inviterPeerId, request, cancellationToken)
            .ConfigureAwait(false);

        var corr = Guid.TryParse(acceptance.Response.RequestCorrelationId, out var parsed) ? parsed : Guid.NewGuid();

        // Chunk H.2: do NOT auto-deliver the response to Main. Queue it so the simulator UI
        // can present an explicit Accept button to trigger delivery.
        await QueueInviteHandshakeResponseForDeliveryToMainAsync(simulatedPeerId, corr, acceptance.Response, cancellationToken)
            .ConfigureAwait(false);

        return new EstablishDirectSessionResponse
        {
            Version = 1,
            Queued = new EstablishDirectSessionResponse.Types.Queued
            {
                Version = 1,
                RequestCorrelationId = corr.ToString()
            }
        };
    }

    public Task DeliverInviteHandshakeResponseToMainAsync(
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default)
        => DeliverInviteHandshakeResponseToMainAsyncCore(response, cancellationToken);

    public async Task<SimulatedPeerInviteAcceptance> AcceptReverseSignalInviteAsync(
        Guid simulatedPeerId,
        Guid inviterPeerId,
        EstablishDirectSessionRequest invite,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (invite is null) throw new ArgumentNullException(nameof(invite));

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var model = _peers.FirstOrDefault(p => p.PeerId == simulatedPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedPeerId}");

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
            inviterOtk = new OneTimeKey(payload.InviterPreKey.InviterOneTimePreKey.ToByteArray());
        }

        var inviterBundle = new Percolator.Cryptography.PreKeyBundle(
            identitySigningKey: new RatchetIdentityKey(invite.InviterIdentityKey.ToByteArray()),
            signedPreKeyId: Guid.Empty,
            signedPreKey: new PreKey(payload.InviterPreKey.InviterSignedPreKey.ToByteArray()),
            signedPreKeySignature: new Signature(payload.InviterPreKey.PreKeySignature.ToByteArray()),
            oneTimePreKeyId: null,
            oneTimePreKey: inviterOtk,
            expirationDateUtc: payload.ExpiresAtUtc?.ToDateTimeOffset());

            var crypto = new AeadSessionCrypto();
            var clock = ResolveClock();

            var localIkPriv = new PrivatePreKey(model.IdentitySigningKeyPrivateKeyEcPrivateKey);
            var x3 = crypto.X3DH_Initiate(localIkPriv, inviterBundle);

            var sessionId = SessionId.NewId();
            var root = new RootKey(x3.SharedSecret.Value);
            var session = RatchetBootstrap.CreateInitiatorSession(
                sessionId,
                new PeerId(inviterPeerId),
                new ProtocolVersion(1),
                root,
                clock,
                crypto: crypto);

            model.SessionsMutable[sessionId] = session;

            var inner = new ResponderInnerHello
            {
                Version = 1,
                DirectSessionId = sessionId.Value.ToString()
            };

            var initial = session.Encrypt(new Plaintext(inner.ToByteArray()), clock);
            model.SessionsMutable[sessionId] = session;

            var response = new InviteHandshakeResponse
            {
                Version = 1,
                RequestCorrelationId = payload.RequestCorrelationId,
                AcceptorIdentityKey = ByteString.CopyFrom(model.IdentitySigningKeySpki),
                AcceptorX3DhEphemeralKey = ByteString.CopyFrom(x3.EphemeralPublic.Value),
                InitialRatchetMessage = ByteString.CopyFrom(initial.Value)
            };

            return new SimulatedPeerInviteAcceptance(sessionId, response);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task ReceiveInviteHandshakeResponseFromMainAsync(
        Guid simulatedPeerId,
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (response is null) throw new ArgumentNullException(nameof(response));

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var model = _peers.FirstOrDefault(p => p.PeerId == simulatedPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedPeerId}");

            var corr = Guid.TryParse(response.RequestCorrelationId, out var parsed) ? parsed : Guid.NewGuid();
            _pending.AddInviteHandshakeResponse(simulatedPeerId, corr, response);
            model.MarkInboundPending(corr);
            return;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task QueueInviteHandshakeResponseForDeliveryToMainAsync(
        Guid simulatedPeerId,
        Guid requestCorrelationId,
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (response is null) throw new ArgumentNullException(nameof(response));

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var model = _peers.FirstOrDefault(p => p.PeerId == simulatedPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedPeerId}");

            _pending.AddInviteHandshakeResponse(simulatedPeerId, requestCorrelationId, response);
            model.MarkInboundPending(requestCorrelationId);
            return;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task<bool> TryDeliverQueuedInviteHandshakeResponseToMainAsync(
        Guid simulatedPeerId,
        Guid requestCorrelationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_pending.TryTakeInviteHandshakeResponse(simulatedPeerId, requestCorrelationId, out var response))
        {
            return false;
        }

        await DeliverInviteHandshakeResponseToMainAsync(response, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<SessionId?> TryFinalizeInviteHandshakeResponseFromMainAsync(
        Guid simulatedPeerId,
        Guid acceptorPeerId,
        Guid requestCorrelationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_pending.TryGetInviteHandshakeResponse(simulatedPeerId, requestCorrelationId, out var response))
        {
            return null;
        }

        SimulatedPeerModel model;
        SimulatedOutboundInviteModel? outbound;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            model = _peers.FirstOrDefault(p => p.PeerId == simulatedPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedPeerId}");

            outbound = model.OutboundInvitesMutable.FirstOrDefault(x => x.CorrelationId == requestCorrelationId);
            if (outbound is null)
            {
                return null;
            }
        }
        finally
        {
            _stateGate.Release();
        }

        if (!response.HasAcceptorIdentityKey || response.AcceptorIdentityKey.Length == 0)
            throw new InvalidOperationException("InviteHandshakeResponse missing acceptor_identity_key");
        if (!response.HasAcceptorX3DhEphemeralKey || response.AcceptorX3DhEphemeralKey.Length == 0)
            throw new InvalidOperationException("InviteHandshakeResponse missing acceptor_x3dh_ephemeral_key");
        if (!response.HasInitialRatchetMessage || response.InitialRatchetMessage.Length == 0)
            throw new InvalidOperationException("InviteHandshakeResponse missing initial_ratchet_message");

        var acceptorIdentityPublic = new RatchetIdentityKey(response.AcceptorIdentityKey.ToByteArray());
        var acceptorEphemeralPublic = new RatchetEphemeralKey(response.AcceptorX3DhEphemeralKey.ToByteArray());

        var crypto = new AeadSessionCrypto();
        var localIkPriv = new PrivatePreKey(model.IdentitySigningKeyPrivateKeyEcPrivateKey);
        var localSpkPriv = new PrivatePreKey(outbound.SignedPreKeyPrivateEcPrivateKey);

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
        catch
        {
            return null;
        }

        var root = new RootKey(shared.Value);

        SessionRatchetMessage ratchetMessage;
        try
        {
            ratchetMessage = new SessionRatchetMessage(response.InitialRatchetMessage.ToByteArray());
        }
        catch
        {
            return null;
        }

        var clock = ResolveClock();
        var tmp = RatchetBootstrap.CreateResponderSession(
            SessionId.NewId(),
            new PeerId(acceptorPeerId),
            new ProtocolVersion(1),
            root,
            clock);

        Plaintext pt;
        try
        {
            pt = tmp.Decrypt(ratchetMessage, clock);
        }
        catch
        {
            return null;
        }

        ResponderInnerHello inner;
        try
        {
            inner = ResponderInnerHello.Parser.ParseFrom(pt.Value);
        }
        catch
        {
            return null;
        }

        if (!inner.HasVersion || inner.Version != 1) return null;
        if (!inner.HasDirectSessionId || string.IsNullOrWhiteSpace(inner.DirectSessionId)) return null;

        SessionId sid;
        try
        {
            sid = new SessionId(Guid.Parse(inner.DirectSessionId));
        }
        catch
        {
            return null;
        }

        var final = SecureSession.Create(
            sid,
            tmp.RemotePeerId,
            tmp.ProtocolVersion,
            tmp.State,
            crypto,
            clock);

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            model.SessionsMutable[sid] = final;
            _ = _pending.TryTakeInviteHandshakeResponse(simulatedPeerId, requestCorrelationId, out _);
            return sid;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task<EstablishSessionResponse> ReceiveEstablishSessionFromMainAsync(
        Guid simulatedPeerId,
        EstablishSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null) throw new ArgumentNullException(nameof(request));

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var model = _peers.FirstOrDefault(p => p.PeerId == simulatedPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedPeerId}");

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
            // Keep simulator tolerant of unknown IDs (matches previous runtime behavior).
            using var identityEcdh = ECDiffieHellman.Create();
            identityEcdh.ImportECPrivateKey(model.IdentitySigningKeyPrivateKeyEcPrivateKey, out _);
            var curve = identityEcdh.ExportParameters(false).Curve;
            using var signedPreKey = ECDiffieHellman.Create(curve);
            var spkSpki = signedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
            var spkPriv = signedPreKey.ExportECPrivateKey();
            spk = new SimulatedSignedPreKeyModel(spkId, spkPriv, spkSpki);
            model.SignedPreKeysMutable.Add(spk);
        }

        var initiatorId = new RatchetIdentityKey(request.IdentitySigningKey.ToByteArray());
        var initiatorEph = new RatchetEphemeralKey(request.EphemeralKey.ToByteArray());

        var crypto = new AeadSessionCrypto();
        var localIkPriv = new PrivatePreKey(model.IdentitySigningKeyPrivateKeyEcPrivateKey);
        var localSpkPriv = new PrivatePreKey(spk.PrivateEcPrivateKey);

        var shared = crypto.X3DH_Respond(
            initiatorId,
            initiatorEph,
            localIkPriv,
            localSpkPriv,
            localOtkPrivate: null);

        var sessionId = SessionId.NewId();
        var root = new RootKey(shared.Value);
        var clock = ResolveClock();
        var session = RatchetBootstrap.CreateResponderSession(
            sessionId,
            Percolator.Cryptography.Primitives.PeerId.NewId(),
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
        Guid simulatedPeerId,
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
            model = _peers.FirstOrDefault(p => p.PeerId == simulatedPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedPeerId}");

            // Best-effort: try to decrypt with any known session (typically 1 per peer in simulator today)
            var cipher = new SessionRatchetMessage(request.Payload.ToByteArray());
            

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
                    if (candidatePt.Value.Length == 0)
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
            env = InternalEnvelope.Parser.ParseFrom(pt.Value);
        }
        catch
        {
            return new DeliverOpaqueMessageResponse { Version = 1, Never = new DeliverOpaqueMessageResponse.Types.Never { Version = 1 } };
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
                popped = await TryPopPreKeyBundleByRecipientPkhAsync(simulatedPeerId, getReq.PublicKeyHash.ToByteArray(), cancellationToken)
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
            var resp = new GetPreKeyBundleResponse
            {
                Version = 1,
                PreKeyBundle = GetPreKeyBundleResponse.Types.PreKeyBundle.Parser.ParseFrom(popped.BundleBytes)
            };

            var responseEnvelope = new InternalEnvelope { GetPreKeyBundleResponse = resp };
            var responsePlain = new Plaintext(responseEnvelope.ToByteArray());
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
                    ResponsePayload = ByteString.CopyFrom(responseCipher.Value)
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
                relayHostPeerId: simulatedPeerId,
                targetPkh: enqueue.RecipientPublicKeyHash.ToByteArray(),
                opaqueBytes: enqueue.MessageBlob.ToByteArray(),
                debugType: "Opaque",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new DeliverOpaqueMessageResponse { Version = 1 };
        }

        return new DeliverOpaqueMessageResponse { Version = 1 };
    }

    public async Task PublishStandardPreKeyBundleToRelayAsync(
        Guid simulatedPeerId,
        Guid relayHostPeerId,
        DateTimeOffset expiresUtc,
        int oneTimeKeyCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        byte[] recipientPublicKeyHash;
        byte[] dtoBytes;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var model = _peers.FirstOrDefault(p => p.PeerId == simulatedPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedPeerId}");

            var bundle = _engine.CreateStandardPreKeyBundle(
                peer: model,
                expiresUtc: expiresUtc,
                oneTimeKeyCount: oneTimeKeyCount);

            var dto = new GetPreKeyBundleResponse.Types.PreKeyBundle
            {
                Version = 1,
                IdentityKey = ByteString.CopyFrom(model.IdentitySigningKeySpki),
                SignedPreKeyId = ByteString.CopyFrom(bundle.SignedPreKeyId.ToByteArray()),
                SignedPreKey = ByteString.CopyFrom(bundle.SignedPreKey.Value),
                PreKeySignature = ByteString.CopyFrom(bundle.SignedPreKeySignature.Value)
            };

            foreach (var bundleOneTimeKey in bundle.OneTimeKeys)        
            {
                dto.OneTimeKeys.Add(new GetPreKeyBundleResponse.Types.OneTimeKey
                {
                    OneTimeKeyId = ByteString.CopyFrom(bundleOneTimeKey.Id.ToByteArray()),
                    KeyBytes = ByteString.CopyFrom(bundleOneTimeKey.Key.Value)
                });
            }

            recipientPublicKeyHash = SHA256.HashData(model.IdentitySigningKeySpki);
            dtoBytes = dto.ToByteArray();
        }
        finally
        {
            _stateGate.Release();
        }

        await PublishPreKeyBundleAsync(
                relayHostPeerId: relayHostPeerId,
                recipientPublicKeyHash: recipientPublicKeyHash,
                logicalOwnerPeerId: simulatedPeerId,
                bundleBytes: dtoBytes,
                expiresUtc: expiresUtc,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.PreKeyBundleFetched,
            $"Pre-key bundle published -> relay={relayHostPeerId.ToString()[..8]} owner={simulatedPeerId.ToString()[..8]}",
            peerId: simulatedPeerId,
            relayHostPeerId: relayHostPeerId);
    }

    public async Task<SessionId?> InitiateStandardHandshakeToMainByRelayPkhAsync(
        Guid simulatedPeerId,
        Guid relayHostPeerId,
        byte[] responderPublicKeyHash,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (responderPublicKeyHash is null) throw new ArgumentNullException(nameof(responderPublicKeyHash));
        if (responderPublicKeyHash.Length == 0) return null;

        var popped = await TryPopPreKeyBundleByRecipientPkhAsync(relayHostPeerId, responderPublicKeyHash, cancellationToken)
            .ConfigureAwait(false);

        if (popped?.BundleBytes is null || popped.BundleBytes.Length == 0)
        {
            return null;
        }

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.PreKeyBundleFetched,
            $"Pre-key bundle fetched <- relay={relayHostPeerId.ToString()[..8]} for={simulatedPeerId.ToString()[..8]}",
            peerId: simulatedPeerId,
            relayHostPeerId: relayHostPeerId);

        GetPreKeyBundleResponse.Types.PreKeyBundle bundleProto;
        try
        {
            bundleProto = GetPreKeyBundleResponse.Types.PreKeyBundle.Parser.ParseFrom(popped.BundleBytes);
        }
        catch
        {
            return null;
        }

        if (!bundleProto.HasIdentityKey || bundleProto.IdentityKey.Length == 0) return null;
        if (!bundleProto.HasSignedPreKeyId || bundleProto.SignedPreKeyId.Length == 0) return null;
        if (!bundleProto.HasSignedPreKey || bundleProto.SignedPreKey.Length == 0) return null;
        if (!bundleProto.HasPreKeySignature || bundleProto.PreKeySignature.Length == 0) return null;

        var actualPkh = SHA256.HashData(bundleProto.IdentityKey.ToByteArray());
        if (!actualPkh.AsSpan().SequenceEqual(responderPublicKeyHash))
        {
            return null;
        }

        Guid signedPreKeyId;
        try
        {
            signedPreKeyId = new Guid(bundleProto.SignedPreKeyId.ToByteArray());
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
                .Select(k => new OneTimeKeyInstance(new Guid(k.OneTimeKeyId.ToByteArray()), new OneTimeKey(k.KeyBytes.ToByteArray())))
                .FirstOrDefault();
        
        var responderBundle = new Percolator.Cryptography.PreKeyBundle(
            identitySigningKey: new RatchetIdentityKey(bundleProto.IdentityKey.ToByteArray()),
            signedPreKeyId: signedPreKeyId,
            signedPreKey: new PreKey(bundleProto.SignedPreKey.ToByteArray()),
            signedPreKeySignature: new Signature(bundleProto.PreKeySignature.ToByteArray()),
            oneTimePreKeyId: oneTimePreKeyInstance?.Id,
            oneTimePreKey: oneTimePreKeyInstance?.Key,
            expirationDateUtc: null);

        byte[] helloBytes;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var model = _peers.FirstOrDefault(p => p.PeerId == simulatedPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedPeerId}");

            var initiated = _engine.TryInitiateStandardHandshake(model, responderBundle);
            if (initiated is null)
            {
                return null;
            }

            model.SetPendingStandardHandshakeToMain(responderPublicKeyHash, initiated.SessionId.Value);

            var hello = new HandshakeInitiatorHello
            {
                Version = 1,
                InitiatorIdentityKeySpki = ByteString.CopyFrom(initiated.InitiatorIdentitySigningKeySpki),
                InitiatorEphemeralKeySpki = ByteString.CopyFrom(initiated.InitiatorEphemeralKeySpki),
                SignedPreKeyId = ByteString.CopyFrom(initiated.SignedPreKeyId.ToByteArray())
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
                relayHostPeerId: relayHostPeerId,
                targetPkh: responderPublicKeyHash,
                opaqueBytes: helloBytes,
                debugType: nameof(HandshakeInitiatorHello),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.StandardHandshakeHelloEnqueued,
            $"Standard handshake hello enqueued -> relay={relayHostPeerId.ToString()[..8]}",
            peerId: simulatedPeerId,
            relayHostPeerId: relayHostPeerId);

        return null;
    }

    private const int OneTimeKeyRequestSanityLimit = 100;

    public async Task UpsertPendingStandardSignalHelloAsync(
        Guid recipientPeerId,
        Guid relayHostPeerId,
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

        var initiatorPkh = SHA256.HashData(hello.InitiatorIdentityKeySpki.ToByteArray());
        var initiatorPkhHex = Convert.ToHexString(initiatorPkh).ToLowerInvariant();

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var model = _peers.FirstOrDefault(p => p.PeerId == recipientPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {recipientPeerId}");

            model.PendingInboundStandardSignalHellosMutable[initiatorPkhHex] = new SimulatedPendingStandardSignalHelloModel(
                RelayHostPeerId: relayHostPeerId,
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
            model.ClearInboundReverseSignalPendingCorrelationId();
        }
        finally
        {
            _stateGate.Release();
        }

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.HandshakeStateTransition,
            $"Standard hello pending: initiator={initiatorPkhHex[..8]}",
            peerId: recipientPeerId,
            relayHostPeerId: relayHostPeerId,
            contextTag: "StandardSignalPending");
    }

    public async Task<bool> TryAcceptPendingStandardSignalHelloAsync(
        Guid recipientPeerId,
        string initiatorPkhHex,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(initiatorPkhHex)) throw new ArgumentNullException(nameof(initiatorPkhHex));

        SimulatedPendingStandardSignalHelloModel? pending;
        Guid relayHostPeerId;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var model = _peers.FirstOrDefault(p => p.PeerId == recipientPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {recipientPeerId}");

            if (!model.PendingInboundStandardSignalHellosMutable.TryGetValue(initiatorPkhHex, out pending))
            {
                return false;
            }

            relayHostPeerId = pending.RelayHostPeerId;
            model.PendingInboundStandardSignalHellosMutable.Remove(initiatorPkhHex);

            var hasAnyPending = model.InboundReverseSignalPendingCorrelationId.CurrentValue is not null
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
                PrekeyId = ByteString.CopyFrom(pending.SignedPreKeyId.ToByteArray())
            };

            if (pending.OneTimePreKeyId.HasValue)
            {
                req.OnetimePrekeyId = ByteString.CopyFrom(pending.OneTimePreKeyId.Value.ToByteArray());
            }

            response = await ReceiveEstablishSessionFromMainAsync(recipientPeerId, req, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _diagnostics.Emit(
                SimulatorDiagnosticEventType.HandshakeStateTransition,
                $"Standard hello accept failed: {ex.Message}",
                peerId: recipientPeerId,
                relayHostPeerId: relayHostPeerId,
                contextTag: "StandardSignalAcceptFailed");
            throw;
        }

        var initiatorPkh = Convert.FromHexString(initiatorPkhHex);
        var initiatorPeerId = await TryGetPeerIdByIdentityPkhAsync(initiatorPkh, cancellationToken).ConfigureAwait(false);

        if (initiatorPeerId.HasValue)
        {
            await EnqueueRelayDownstreamToPeerAsync(
                    relayHostPeerId: relayHostPeerId,
                    targetPkh: initiatorPkh,
                    opaqueBytes: response.ToByteArray(),
                    debugType: nameof(EstablishSessionResponse),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await EnqueueRelayUpstreamToMainAsync(
                    relayHostPeerId: relayHostPeerId,
                    opaqueBytes: response.ToByteArray(),
                    debugType: nameof(EstablishSessionResponse),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.HandshakeStateTransition,
            $"Standard hello accepted: initiator={initiatorPkhHex[..8]}",
            peerId: recipientPeerId,
            relayHostPeerId: relayHostPeerId,
            contextTag: "StandardSignalAccepted");

        return true;
    }

    private Task<EstablishSessionResponse> DeliverEstablishSessionToMainAsync(
        EstablishSessionRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        using var scope = _scopeFactory.CreateScope();
        var messageService = scope.ServiceProvider.GetRequiredService<Percolator.Application.Network.PercolatorMessageService>();

        var ctx = new ServerCallContextStub(
            method: "/percolator.contracts.TransportService/EstablishSession",
            peer: "ipv4:127.0.0.1:0",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: new Metadata(),
            cancellationToken: cancellationToken);

        return messageService.EstablishSession(request, ctx);
    }

    public async Task<byte[]> ComputePublicKeyHashAsync(Guid simulatedPeerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var model = _peers.FirstOrDefault(p => p.PeerId == simulatedPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedPeerId}");
            return SHA256.HashData(model.IdentitySigningKeySpki);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task<SessionRatchetMessage> EncryptInternalEnvelopeAsync(
        Guid simulatedPeerId,
        SessionId sessionId,
        InternalEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var model = _peers.FirstOrDefault(p => p.PeerId == simulatedPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedPeerId}");

            var plaintext = new Plaintext(envelope.ToByteArray());
            var cipher = _engine.Encrypt(model, sessionId, plaintext);
            return cipher;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task<Plaintext> DecryptSessionMessageAsync(
        Guid simulatedPeerId,
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
                var model = _peers.FirstOrDefault(p => p.PeerId == simulatedPeerId)
                    ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedPeerId}");

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
                peerId: simulatedPeerId);
            throw;
        }
    }

    private SimulatedRelayModel GetRelayOrThrow(Guid relayHostPeerId)
    {
        if (_relayByHostPeerId.TryGetValue(relayHostPeerId, out var relay)) return relay;
        throw new InvalidOperationException($"No relay exists with host peer id {relayHostPeerId}");
    }

    public async Task<bool> DeleteRelayMessageByAckIdAsync(Guid relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool removed;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = GetRelayOrThrow(relayHostPeerId);
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

    public async Task<bool> MoveRelayMessageByAckIdAsync(Guid relayHostPeerId, Guid ackId, int delta, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (delta == 0) return false;

        var changed = false;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = GetRelayOrThrow(relayHostPeerId);
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
                newTime = DateTimeOffset.UtcNow;
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
                relayHostPeerId: relayHostPeerId,
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

    public async Task<bool> CorruptRelayMessageByAckIdAsync(Guid relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var changed = false;
        var result = false;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = GetRelayOrThrow(relayHostPeerId);

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
                relayHostPeerId: relayHostPeerId,
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
        Guid relayHostPeerId,
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
            var relay = GetRelayOrThrow(relayHostPeerId);

            var msg = new OutboundRelayMessage(
                AckId: Guid.NewGuid(),
                OpaqueBytes: opaqueBytes,
                EnqueuedUtc: DateTimeOffset.UtcNow,
                DebugType: debugType);

            relay.EnqueueMessage(msg);

            _diagnostics.Emit(
                SimulatorDiagnosticEventType.RelayEnqueued,
                $"Relay enqueue: {(debugType ?? "opaque")}",
                relayHostPeerId: relayHostPeerId,
                ackId: msg.AckId);
        }
        finally
        {
            _stateGate.Release();
        }

        _saveTrigger.OnNext(Unit.Default);
    }

    public async Task EnqueueRelayDownstreamToPeerAsync(
        Guid relayHostPeerId,
        byte[] targetPkh,
        byte[] opaqueBytes,
        string? debugType = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (targetPkh is null) throw new ArgumentNullException(nameof(targetPkh));
        if (targetPkh.Length == 0) throw new ArgumentException("TargetPkh must be non-empty", nameof(targetPkh));
        if (opaqueBytes is null) throw new ArgumentNullException(nameof(opaqueBytes));
        if (opaqueBytes.Length == 0) return;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = GetRelayOrThrow(relayHostPeerId);

            var msg = new InboundRelayMessage(
                AckId: Guid.NewGuid(),
                TargetPkh: targetPkh,
                OpaqueBytes: opaqueBytes,
                EnqueuedUtc: DateTimeOffset.UtcNow,
                DebugType: debugType);

            relay.EnqueueMessage(msg);

            _diagnostics.Emit(
                SimulatorDiagnosticEventType.RelayEnqueued,
                $"Relay enqueue: {(debugType ?? "opaque")}",
                relayHostPeerId: relayHostPeerId,
                ackId: msg.AckId);
        }
        finally
        {
            _stateGate.Release();
        }

        _saveTrigger.OnNext(Unit.Default);
    }

    public async Task<IReadOnlyList<InboundRelayMessage>> DequeueRelayDownstreamToPeerAsync(
        Guid relayHostPeerId,
        byte[] targetPkh,
        int max,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (targetPkh is null) throw new ArgumentNullException(nameof(targetPkh));
        if (targetPkh.Length == 0) return Array.Empty<InboundRelayMessage>();
        if (max <= 0) return Array.Empty<InboundRelayMessage>();

        List<InboundRelayMessage> snapshot;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = GetRelayOrThrow(relayHostPeerId);
            snapshot = relay.MessageQueue
                .Select(kvp => kvp.Value)
                .OfType<InboundRelayMessage>()
                .Where(x => x.TargetPkh.AsSpan().SequenceEqual(targetPkh))
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
        Guid relayHostPeerId,
        SessionId relayHostToMainSessionId,
        int max,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (max <= 0) return 0;

        using var scope = _scopeFactory.CreateScope();
        var messageService = scope.ServiceProvider.GetRequiredService<Percolator.Application.Network.PercolatorMessageService>();

        var forwarded = 0;
        while (forwarded < max)
        {
            cancellationToken.ThrowIfCancellationRequested();

            OutboundRelayMessage? next;

            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var relay = GetRelayOrThrow(relayHostPeerId);
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

                var cipher = await EncryptInternalEnvelopeAsync(relayHostPeerId, relayHostToMainSessionId, env, cancellationToken)
                    .ConfigureAwait(false);

                var request = new DeliverOpaqueMessageRequest
                {
                    Version = 1,
                    Payload = ByteString.CopyFrom(cipher.Value)
                };

                var ctx = new ServerCallContextStub(
                    method: "/percolator.contracts.TransportService/DeliverOpaqueMessage",
                    peer: "ipv4:127.0.0.1:0",
                    deadline: DateTime.UtcNow.AddMinutes(1),
                    requestHeaders: new Metadata(),
                    cancellationToken: cancellationToken);

                var resp = await messageService.DeliverOpaqueMessage(request, ctx).ConfigureAwait(false);

                if (resp.ResultCase != DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload
                    || resp.ResponsePayload is null
                    || !resp.ResponsePayload.HasResponsePayload
                    || resp.ResponsePayload.ResponsePayload.Length == 0)
                {
                    await EnqueueRelayUpstreamToMainAsync(relayHostPeerId, next.OpaqueBytes, next.DebugType, cancellationToken).ConfigureAwait(false);
                    break;
                }

                var ackCipher = new SessionRatchetMessage(resp.ResponsePayload.ResponsePayload.ToByteArray());
                var ackPlain = await DecryptSessionMessageAsync(relayHostPeerId, relayHostToMainSessionId, ackCipher, cancellationToken)
                    .ConfigureAwait(false);
                var ack = RelayOpaqueResponse.Parser.ParseFrom(ackPlain.Value);
                if (!ack.HasMessageAckId || ack.MessageAckId.Length == 0)
                {
                    await EnqueueRelayUpstreamToMainAsync(relayHostPeerId, next.OpaqueBytes, next.DebugType, cancellationToken).ConfigureAwait(false);
                    break;
                }

                var returned = new Guid(ack.MessageAckId.ToByteArray());
                if (returned != next.AckId)
                {
                    await EnqueueRelayUpstreamToMainAsync(relayHostPeerId, next.OpaqueBytes, next.DebugType, cancellationToken).ConfigureAwait(false);
                    break;
                }

                forwarded++;
            }
            catch
            {
                await EnqueueRelayUpstreamToMainAsync(relayHostPeerId, next.OpaqueBytes, next.DebugType, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        _saveTrigger.OnNext(Unit.Default);

        return forwarded;
    }

    public async Task<bool> DeliverRelayUpstreamToMainByAckIdAsync(
        Guid relayHostPeerId,
        SessionId relayHostToMainSessionId,
        Guid ackId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        OutboundRelayMessage? msg;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = GetRelayOrThrow(relayHostPeerId);
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

            var cipher = await EncryptInternalEnvelopeAsync(relayHostPeerId, relayHostToMainSessionId, env, cancellationToken)
                .ConfigureAwait(false);

            using var scope = _scopeFactory.CreateScope();
            var messageService = scope.ServiceProvider.GetRequiredService<Percolator.Application.Network.PercolatorMessageService>();

            var request = new DeliverOpaqueMessageRequest
            {
                Version = 1,
                Payload = ByteString.CopyFrom(cipher.Value)
            };

            var ctx = new ServerCallContextStub(
                method: "/percolator.contracts.TransportService/DeliverOpaqueMessage",
                peer: "ipv4:127.0.0.1:0",
                deadline: DateTime.UtcNow.AddMinutes(1),
                requestHeaders: new Metadata(),
                cancellationToken: cancellationToken);

            var resp = await messageService.DeliverOpaqueMessage(request, ctx).ConfigureAwait(false);
            if (resp.ResultCase != DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload
                || resp.ResponsePayload is null
                || !resp.ResponsePayload.HasResponsePayload
                || resp.ResponsePayload.ResponsePayload.Length == 0)
            {
                await EnqueueRelayUpstreamToMainAsync(relayHostPeerId, msg.OpaqueBytes, msg.DebugType, cancellationToken).ConfigureAwait(false);
                return false;
            }

            var ackCipher = new SessionRatchetMessage(resp.ResponsePayload.ResponsePayload.ToByteArray());
            var ackPlain = await DecryptSessionMessageAsync(relayHostPeerId, relayHostToMainSessionId, ackCipher, cancellationToken)
                .ConfigureAwait(false);
            var ack = RelayOpaqueResponse.Parser.ParseFrom(ackPlain.Value);
            if (!ack.HasMessageAckId || ack.MessageAckId.Length == 0)
            {
                await EnqueueRelayUpstreamToMainAsync(relayHostPeerId, msg.OpaqueBytes, msg.DebugType, cancellationToken).ConfigureAwait(false);
                return false;
            }

            var returned = new Guid(ack.MessageAckId.ToByteArray());
            if (returned != msg.AckId)
            {
                await EnqueueRelayUpstreamToMainAsync(relayHostPeerId, msg.OpaqueBytes, msg.DebugType, cancellationToken).ConfigureAwait(false);
                return false;
            }

            return true;
        }
        catch
        {
            await EnqueueRelayUpstreamToMainAsync(relayHostPeerId, msg.OpaqueBytes, msg.DebugType, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<EstablishSessionResponse?> ReceiveRelayedOpaquePayloadAsync(
        Guid simulatedPeerId,
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

                var responderPkh = SHA256.HashData(resp.Response.IdentitySigningKey.ToByteArray());
                await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var model = _peers.FirstOrDefault(p => p.PeerId == simulatedPeerId)
                        ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedPeerId}");

                    var pendingPkh = model.PendingStandardHandshakeToMainResponderPublicKeyHash.CurrentValue;
                    var pendingSidGuid = model.PendingStandardHandshakeToMainTemporarySessionId.CurrentValue;
                    if (pendingPkh is null || pendingPkh.Length == 0 || pendingSidGuid is null)
                    {
                        return null;
                    }
                    if (!pendingPkh.AsSpan().SequenceEqual(responderPkh))
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

                    model.ClearPendingStandardHandshakeToMain();
                }
                finally
                {
                    _stateGate.Release();
                }

                _diagnostics.Emit(
                    SimulatorDiagnosticEventType.HandshakeStateTransition,
                    $"Standard handshake established: sid={assignedGuid.ToString()[..8]}",
                    peerId: simulatedPeerId,
                    contextTag: "Established");

                return null;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private async Task DeliverInviteHandshakeResponseToMainAsyncCore(
        InviteHandshakeResponse response,
        CancellationToken cancellationToken)
    {
        if (response is null) throw new ArgumentNullException(nameof(response));

        using var scope = _scopeFactory.CreateScope();
        var messageService = scope.ServiceProvider.GetRequiredService<Percolator.Application.Network.PercolatorMessageService>();

        var ctx = new ServerCallContextStub(
            method: "/percolator.contracts.TransportService/DeliverInviteHandshakeResponse",
            peer: "ipv4:127.0.0.1:0",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: new Metadata(),
            cancellationToken: cancellationToken);

        await messageService.DeliverInviteHandshakeResponse(response, ctx).ConfigureAwait(false);
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
                    _peerById[model.PeerId] = model;
                    AttachRuntimePersistence(model);
                }

                _relationships.Clear();
                foreach (var rel in snapshot.Relationships)
                {
                    _relationships.Add(new PeerRelationship(rel.SourcePeerId, rel.TargetPeerId, rel.Type));
                }

                _relays.Clear();
                _relayByHostPeerId.Clear();
                foreach (var relaySnap in snapshot.Relays)
                {
                    var relay = CreateRelayFromSnapshot(relaySnap);
                    _relays.Add(relay);
                    _relayByHostPeerId[relay.RelayHostPeerId] = relay;
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
        return new SimulatedPeerModel(
            peerId: snap.PeerId,
            selfIdentityId: snap.SelfIdentityId,
            displayName: snap.DisplayName,
            isOnline: snap.IsOnline,
            isRelayCapable: snap.IsRelayCapable,
            identitySigningKeySpki: snap.IdentitySigningKeySpki,
            identitySigningKeyPrivateKeyEcPrivateKey: snap.IdentitySigningKeyPrivateKeyEcPrivateKey,
            connectionMode: snap.ConnectionMode,
            host: snap.Host,
            port: snap.Port,
            relayPeerId: snap.RelayPeerId == Guid.Empty ? null : snap.RelayPeerId,
            uiState: snap.UiState,
            pendingCorrelationId: snap.InboundReverseSignalPendingCorrelationId,
            targetPublicKeyHash: snap.TargetPublicKeyHash,
            selectedRouteMode: snap.SelectedRouteMode,
            directEndpoint: snap.DirectEndpoint,
            relayHostPeerId: snap.RelayHostPeerId,
            phase: snap.Phase,
            notUntilUtc: snap.NotUntilUtc,
            lastError: snap.LastError,
            handshakeAttempts: snap.HandshakeAttempts.ToList(),
            pendingStandardHandshakeToMainResponderPublicKeyHash: snap.PendingStandardHandshakeToMainResponderPublicKeyHash,
            pendingStandardHandshakeToMainTemporarySessionId: snap.PendingStandardHandshakeToMainTemporarySessionId,
            knownPeerIds: snap.KnownPeerIds.ToList(),
            publishedPreKeyBundles: snap.PublishedPreKeyBundles
                .Select(b => new SimulatedPublishedPreKeyBundleModel(
                    b.RecipientPublicKeyHash,
                    b.LogicalOwnerPeerId,
                    b.BundleBytes,
                    b.ExpiresUtc))
                .ToList());
    }

    private static SimulatedRelayModel CreateRelayFromSnapshot(RelayStateSnapshot snap)
    {
        var relay = new SimulatedRelayModel(snap.RelayHostPeerId);
        foreach (var m in snap.UpstreamToMain)
        {
            if (m.AckId == Guid.Empty) continue;
            relay.EnqueueMessage(new OutboundRelayMessage(m.AckId, m.OpaqueBytes, m.EnqueuedUtc, m.DebugType));
        }
        foreach (var m in snap.DownstreamToPeers)
        {
            if (m.AckId == Guid.Empty) continue;
            if (m.TargetPkh is null || m.TargetPkh.Length == 0) continue;
            relay.EnqueueMessage(new InboundRelayMessage(m.AckId, m.TargetPkh, m.OpaqueBytes, m.EnqueuedUtc, m.DebugType));
        }
        return relay;
    }

    public async Task<Guid> AddPeerAsync(string? displayName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var peerId = Guid.NewGuid();

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
            var host = AllocateNextLoopbackHostOnPeerGate();
            var port = _transportOptions.Value.SimulatorPort;
            if (port == 0) port = 5002;

            var selfIdentityId = Interlocked.Increment(ref _nextSelfIdentityId);

            model = new SimulatedPeerModel(
                peerId: peerId,
                selfIdentityId: selfIdentityId,
                displayName: name,
                isOnline: true,
                isRelayCapable: false,
                identitySigningKeySpki: spki,
                identitySigningKeyPrivateKeyEcPrivateKey: priv,
                connectionMode: ConnectionMode.Direct,
                host: host,
                port: port,
                relayPeerId: null);

            _peerById[peerId] = model;
            _peers.Add(model);
            AttachRuntimePersistence(model);
        }
        finally
        {
            _stateGate.Release();
        }

        _saveTrigger.OnNext(Unit.Default);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.PeerCreated,
            $"Peer created: {(string.IsNullOrWhiteSpace(model.DisplayName.CurrentValue) ? model.PeerId.ToString()[..8] : model.DisplayName.CurrentValue)}",
            peerId: peerId);

        return peerId;
    }

    private string AllocateNextLoopbackHostOnPeerGate()
    {
        // Must be called under _stateGate.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _peers)
        {
            var host = p.Host.CurrentValue;
            if (!string.IsNullOrWhiteSpace(host))
            {
                used.Add(host);
            }
        }

        for (var x = 1; x <= 254; x++)
        {
            for (var y = 1; y <= 254; y++)
            {
                var candidate = $"127.77.{x}.{y}";
                if (!used.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        // Fallback: extremely unlikely. Preserve previous deterministic mapping.
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Guid.NewGuid().ToByteArray());
        var fx = (byte)((hash[0] % 254) + 1);
        var fy = (byte)((hash[1] % 254) + 1);
        return $"127.77.{fx}.{fy}";
    }

    public Task<Guid?> TryGetPeerIdByIdentityPkhAsync(byte[] recipientPublicKeyHash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (recipientPublicKeyHash is null) throw new ArgumentNullException(nameof(recipientPublicKeyHash));
        if (recipientPublicKeyHash.Length == 0) return Task.FromResult<Guid?>(null);

        List<Guid> matches;
        _stateGate.Wait(cancellationToken);
        try
        {
            matches = _peerById.Values
                .Where(p => SHA256.HashData(p.IdentitySigningKeySpki).SequenceEqual(recipientPublicKeyHash))
                .Select(p => p.PeerId)
                .Take(2)
                .ToList();
        }
        finally
        {
            _stateGate.Release();
        }

        if (matches.Count != 1) return Task.FromResult<Guid?>(null);
        return Task.FromResult<Guid?>(matches[0]);
    }

    public async Task RemovePeerAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string name;
        SimulatedPeerModel? removedModel = null;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_peerById.Remove(peerId, out removedModel)) return;
            name = string.IsNullOrWhiteSpace(removedModel.DisplayName.CurrentValue) ? removedModel.PeerId.ToString()[..8] : removedModel.DisplayName.CurrentValue;

            for (var i = _relationships.Count - 1; i >= 0; i--)
            {
                var rel = _relationships[i];
                if (rel.SourcePeerId == peerId || rel.TargetPeerId == peerId)
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

        await RemoveRelayIfExistsAsync(peerId, cancellationToken).ConfigureAwait(false);

        removedModel?.Dispose();

        _saveTrigger.OnNext(Unit.Default);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.PeerRemoved,
            $"Peer removed: {name}",
            peerId: peerId);
    }

    public async Task AddPublishedKeysRelationshipAsync(Guid publisherPeerId, Guid hostPeerId, CancellationToken cancellationToken = default)
    {
        if (publisherPeerId == hostPeerId) return;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_peerById.ContainsKey(publisherPeerId)) return;
            if (!_peerById.ContainsKey(hostPeerId)) return;

            var rel = new PeerRelationship(publisherPeerId, hostPeerId, RelationshipType.PublishedKey);
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
            $"Pre-keys relationship added: {publisherPeerId.ToString()[..8]} -> {hostPeerId.ToString()[..8]}",
            peerId: publisherPeerId);
    }

    public async Task RemovePublishedKeysRelationshipAsync(Guid publisherPeerId, Guid hostPeerId, CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rel = new PeerRelationship(publisherPeerId, hostPeerId, RelationshipType.PublishedKey);
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
            $"Pre-keys relationship removed: {publisherPeerId.ToString()[..8]} -> {hostPeerId.ToString()[..8]}",
            peerId: publisherPeerId);
    }

    public async Task AddRelayActiveSessionAsync(Guid relayHostPeerId, Guid peerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (relayHostPeerId == peerId) return;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_peerById.ContainsKey(relayHostPeerId)) return;
            if (!_peerById.ContainsKey(peerId)) return;

            var rel = new PeerRelationship(relayHostPeerId, peerId, RelationshipType.RelayActiveSession);
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
            $"Relay active session added: relay={relayHostPeerId.ToString()[..8]} peer={peerId.ToString()[..8]}",
            peerId: peerId,
            relayHostPeerId: relayHostPeerId);
    }

    public async Task RemoveRelayActiveSessionAsync(Guid relayHostPeerId, Guid peerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (relayHostPeerId == peerId) return;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rel = new PeerRelationship(relayHostPeerId, peerId, RelationshipType.RelayActiveSession);
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
            $"Relay active session removed: relay={relayHostPeerId.ToString()[..8]} peer={peerId.ToString()[..8]}",
            peerId: peerId,
            relayHostPeerId: relayHostPeerId);
    }

    public async Task PublishPreKeyBundleAsync(
        Guid relayHostPeerId,
        byte[] recipientPublicKeyHash,
        Guid logicalOwnerPeerId,
        byte[] bundleBytes,
        DateTimeOffset expiresUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (recipientPublicKeyHash is null) throw new ArgumentNullException(nameof(recipientPublicKeyHash));
        if (bundleBytes is null) throw new ArgumentNullException(nameof(bundleBytes));

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_peerById.TryGetValue(relayHostPeerId, out var host)) return;

            host.PublishedPreKeyBundles.Add(new SimulatedPublishedPreKeyBundleModel(
                RecipientPublicKeyHash: recipientPublicKeyHash,
                LogicalOwnerPeerId: logicalOwnerPeerId,
                BundleBytes: bundleBytes,
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
            peerId: logicalOwnerPeerId,
            relayHostPeerId: relayHostPeerId,
            contextTag: Convert.ToBase64String(recipientPublicKeyHash));
    }

    private async Task<SimulatedPublishedPreKeyBundleModel?> TryPopPreKeyBundleByRecipientPkhAsync(
        Guid relayHostPeerId,
        byte[] recipientPublicKeyHash,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (recipientPublicKeyHash is null) throw new ArgumentNullException(nameof(recipientPublicKeyHash));

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_peerById.TryGetValue(relayHostPeerId, out var host)) return null;

            var now = DateTimeOffset.UtcNow;
            for (var i = host.PublishedPreKeyBundles.Count - 1; i >= 0; i--)
            {
                if (host.PublishedPreKeyBundles[i].ExpiresUtc <= now)
                {
                    host.PublishedPreKeyBundles.RemoveAt(i);
                }
            }

            var match = host.PublishedPreKeyBundles
                .FirstOrDefault(b => b.RecipientPublicKeyHash.SequenceEqual(recipientPublicKeyHash));

            if (match is null)
            {
                return null;
            }

            host.PublishedPreKeyBundles.Remove(match);
            return match;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private void AttachRuntimePersistence(SimulatedPeerModel model)
    {
        if (_runtimePersistenceByPeerId.ContainsKey(model.PeerId)) return;

        var tracker = new SimulatedPeerRuntimeTracker(model);

        var saveSub = tracker.Dirty.Subscribe(_ => _saveTrigger.OnNext(Unit.Default));

        var lifecycleSub = model.IsRelayCapable
            .Skip(1) // Prevent double-loading during InitializeCoreAsync
            .DistinctUntilChanged()
            .SubscribeAwait(async (enabled, ct) => await OnRelayCapabilityChangedAsync(model.PeerId, enabled, ct).ConfigureAwait(false), AwaitOperation.Sequential);

        _runtimePersistenceByPeerId[model.PeerId] = new CompositeDisposable(tracker, saveSub, lifecycleSub);
    }

    private async Task OnRelayCapabilityChangedAsync(Guid peerId, bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!enabled)
        {
            await RemoveRelayIfExistsAsync(peerId, cancellationToken).ConfigureAwait(false);
            return;
        }

        SimulatedRelayModel? addedRelay = null;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_relayByHostPeerId.ContainsKey(peerId)) return;

            var relay = new SimulatedRelayModel(peerId);
            _relayByHostPeerId[peerId] = relay;
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
        if (_relayPersistenceByHostPeerId.ContainsKey(relay.RelayHostPeerId)) return;

        var tracker = new SimulatedRelayProtocolStateTracker(relay);
        var saveSub = tracker.Dirty.Subscribe(_ => _saveTrigger.OnNext(Unit.Default));
        _relayPersistenceByHostPeerId[relay.RelayHostPeerId] = new CompositeDisposable(tracker, saveSub);
    }

    private async Task RemoveRelayIfExistsAsync(Guid relayHostPeerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SimulatedRelayModel? relay = null;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_relayByHostPeerId.TryGetValue(relayHostPeerId, out relay)) return;

            _relayByHostPeerId.Remove(relayHostPeerId);

            if (_relayPersistenceByHostPeerId.Remove(relayHostPeerId, out var d))
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
