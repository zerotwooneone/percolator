using Google.Protobuf;
using Grpc.Core;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ObservableCollections;
using Percolator.Application.Configuration;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Desktop.Wpf.Features.Simulator.Tracking;
using Desktop.Wpf.Features.Simulator.Models;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorStateService : ISimulatorStateService
{
    private readonly ISimulatorStateRepository _store;
    private readonly ISimulatorDiagnosticsService _diagnostics;
    private readonly ISimulatedPeerPendingInbox _pending;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Desktop.Wpf.Features.Simulator.Protocol.ISignalProtocolEngine _engine;

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

    private readonly object _initGate = new();
    private Task? _initializeTask;

    private readonly SemaphoreSlim _peerGate = new(1, 1);
    private readonly SemaphoreSlim _relayGate = new(1, 1);

    public SimulatorStateService(
        ISimulatorStateRepository store,
        ISimulatorDiagnosticsService diagnostics,
        ISimulatedPeerPendingInbox pending,
        IServiceScopeFactory scopeFactory,
        Desktop.Wpf.Features.Simulator.Protocol.ISignalProtocolEngine engine)
    {
        _store = store;
        _diagnostics = diagnostics;
        _pending = pending;
        _scopeFactory = scopeFactory;
        _engine = engine;

        _saveTrigger
            .Debounce(TimeSpan.FromMilliseconds(250))
            .SelectAwait(async (_, ct) =>
            {
                await _peerGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var peerSnaps = _peers.Select(p => p.Freeze()).ToList();
                    var relSnaps = _relationships.Select(r => new PeerRelationshipSnapshot(r.SourcePeerId, r.TargetPeerId, r.Type)).ToList();
                    return (peers: peerSnaps, rels: relSnaps);
                }
                finally
                {
                    _peerGate.Release();
                }
            })
            .SubscribeAwait(async (snaps, ct) => await _store.SavePeersAsync(snaps.peers, snaps.rels, ct).ConfigureAwait(false), AwaitOperation.Sequential)
            .AddTo(ref _bag);
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

        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _peerGate.Release();
        }
    }

    public async Task ReceiveInviteHandshakeResponseFromMainAsync(
        Guid simulatedPeerId,
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (response is null) throw new ArgumentNullException(nameof(response));

        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _peerGate.Release();
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

        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _peerGate.Release();
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

        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _peerGate.Release();
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

        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            model.SessionsMutable[sid] = final;
            _ = _pending.TryTakeInviteHandshakeResponse(simulatedPeerId, requestCorrelationId, out _);
            return sid;
        }
        finally
        {
            _peerGate.Release();
        }
    }

    public async Task<EstablishSessionResponse> ReceiveEstablishSessionFromMainAsync(
        Guid simulatedPeerId,
        EstablishSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null) throw new ArgumentNullException(nameof(request));

        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _peerGate.Release();
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
        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _peerGate.Release();
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

            var resp = new GetPreKeyBundleResponse { Version = 1 };
            if (popped is not null && popped.BundleBytes is not null && popped.BundleBytes.Length > 0)
            {
                try
                {
                    resp.PreKeyBundle = GetPreKeyBundleResponse.Types.PreKeyBundle.Parser.ParseFrom(popped.BundleBytes);
                }
                catch
                {
                    // best-effort: treat parse failure as not found
                }
            }

            var responseEnvelope = new InternalEnvelope { GetPreKeyBundleResponse = resp };
            var responsePlain = new Plaintext(responseEnvelope.ToByteArray());
            var responseCipher = matched.Encrypt(responsePlain, clock);

            await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                model.SessionsMutable[matchedSessionId] = matched;
            }
            finally
            {
                _peerGate.Release();
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
        bool includeOneTimeKeys,
        int oneTimeKeyCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        byte[] recipientPublicKeyHash;
        byte[] dtoBytes;

        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var model = _peers.FirstOrDefault(p => p.PeerId == simulatedPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedPeerId}");

            var bundle = _engine.CreateStandardPreKeyBundle(
                peer: model,
                expiresUtc: expiresUtc,
                includeOneTimeKeys: includeOneTimeKeys,
                oneTimeKeyCount: oneTimeKeyCount);

            var dto = new GetPreKeyBundleResponse.Types.PreKeyBundle
            {
                Version = 1,
                IdentityKey = ByteString.CopyFrom(model.IdentitySigningKeySpki),
                SignedPreKeyId = ByteString.CopyFrom(bundle.SignedPreKeyId.ToByteArray()),
                SignedPreKey = ByteString.CopyFrom(bundle.SignedPreKey.Value),
                PreKeySignature = ByteString.CopyFrom(bundle.SignedPreKeySignature.Value)
            };

            if (bundle.OneTimePreKeyId is not null && bundle.OneTimePreKey is not null)
            {
                dto.OneTimeKeyId = ByteString.CopyFrom(bundle.OneTimePreKeyId.Value.ToByteArray());
                dto.OneTimeKey = ByteString.CopyFrom(bundle.OneTimePreKey.Value);
            }

            recipientPublicKeyHash = SHA256.HashData(model.IdentitySigningKeySpki);
            dtoBytes = dto.ToByteArray();
        }
        finally
        {
            _peerGate.Release();
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

        Guid? oneTimePreKeyId = null;
        OneTimeKey? oneTimePreKey = null;
        if (bundleProto.HasOneTimeKeyId && bundleProto.OneTimeKeyId.Length > 0
            && bundleProto.HasOneTimeKey && bundleProto.OneTimeKey.Length > 0)
        {
            try
            {
                oneTimePreKeyId = new Guid(bundleProto.OneTimeKeyId.ToByteArray());
                oneTimePreKey = new OneTimeKey(bundleProto.OneTimeKey.ToByteArray());
            }
            catch
            {
                oneTimePreKeyId = null;
                oneTimePreKey = null;
            }
        }

        var responderBundle = new Percolator.Cryptography.PreKeyBundle(
            identitySigningKey: new RatchetIdentityKey(bundleProto.IdentityKey.ToByteArray()),
            signedPreKeyId: signedPreKeyId,
            signedPreKey: new PreKey(bundleProto.SignedPreKey.ToByteArray()),
            signedPreKeySignature: new Signature(bundleProto.PreKeySignature.ToByteArray()),
            oneTimePreKeyId: oneTimePreKeyId,
            oneTimePreKey: oneTimePreKey,
            expirationDateUtc: null);

        byte[] helloBytes;
        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _peerGate.Release();
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

        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var model = _peers.FirstOrDefault(p => p.PeerId == simulatedPeerId)
                ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedPeerId}");
            return SHA256.HashData(model.IdentitySigningKeySpki);
        }
        finally
        {
            _peerGate.Release();
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

        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _peerGate.Release();
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
            await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var model = _peers.FirstOrDefault(p => p.PeerId == simulatedPeerId)
                    ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedPeerId}");

                var pt = _engine.Decrypt(model, sessionId, message);
                return pt;
            }
            finally
            {
                _peerGate.Release();
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

    private async Task InitializeRelaysAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Greenfield relay persistence: load per-relay state into pure models.
        // Only create relay models for relay-capable peers.
        List<Guid> relayCapablePeerIds;
        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            relayCapablePeerIds = _peers
                .Where(p => p.IsRelayCapable.Value)
                .Select(p => p.PeerId)
                .ToList();
        }
        finally
        {
            _peerGate.Release();
        }

        foreach (var d in _relayPersistenceByHostPeerId.Values)
        {
            d.Dispose();
        }
        _relayPersistenceByHostPeerId.Clear();

        await _relayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _relayByHostPeerId.Clear();
            _relays.Clear();
        }
        finally
        {
            _relayGate.Release();
        }

        foreach (var hostPeerId in relayCapablePeerIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relay = await _store.LoadRelayAsync(hostPeerId, cancellationToken).ConfigureAwait(false)
                ?? new SimulatedRelayModel(hostPeerId);

            await _relayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _relayByHostPeerId[hostPeerId] = relay;
                AttachRelayPersistence(relay);
            }
            finally
            {
                _relayGate.Release();
            }

            await _relayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _relays.Add(relay);
            }
            finally
            {
                _relayGate.Release();
            }
        }
    }

    private void AttachRelayPersistence(SimulatedRelayModel relay)
    {
        if (_relayPersistenceByHostPeerId.ContainsKey(relay.RelayHostPeerId)) return;

        var tracker = new SimulatedRelayProtocolStateTracker(relay);
        var sub = tracker.Dirty
            .Debounce(TimeSpan.FromMilliseconds(200))
            .SubscribeAwait(async (_, ct) => await PersistRelayAsync(relay, ct).ConfigureAwait(false), AwaitOperation.Sequential);

        _relayPersistenceByHostPeerId[relay.RelayHostPeerId] = new CompositeDisposable(tracker, sub);
    }

    private Task PersistRelayAsync(SimulatedRelayModel relay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return PersistRelayFrozenAsync(relay, cancellationToken);
    }

    private async Task PersistRelayFrozenAsync(SimulatedRelayModel relay, CancellationToken cancellationToken)
    {
        RelayStateSnapshot frozen;
        await _relayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            frozen = relay.Freeze();
        }
        finally
        {
            _relayGate.Release();
        }

        await _store.SaveRelayAsync(frozen, cancellationToken).ConfigureAwait(false);
    }

    private SimulatedRelayModel GetRelayOrThrow(Guid relayHostPeerId)
    {
        if (_relayByHostPeerId.TryGetValue(relayHostPeerId, out var relay)) return relay;
        throw new InvalidOperationException($"No relay exists with host peer id {relayHostPeerId}");
    }

    public async Task<bool> DeleteRelayMessageByAckIdAsync(Guid relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _relayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = GetRelayOrThrow(relayHostPeerId);
            return relay.RemoveMessage(ackId);
        }
        finally
        {
            _relayGate.Release();
        }
    }

    public async Task<bool> MoveRelayMessageByAckIdAsync(Guid relayHostPeerId, Guid ackId, int delta, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (delta == 0) return false;

        await _relayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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

            return true;
        }
        finally
        {
            _relayGate.Release();
        }
    }

    public async Task<bool> CorruptRelayMessageByAckIdAsync(Guid relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _relayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                        break;
                    }
                    case InboundRelayMessage inMsg:
                    {
                        if (inMsg.OpaqueBytes is null || inMsg.OpaqueBytes.Length == 0) return false;
                        var bytes = inMsg.OpaqueBytes.ToArray();
                        bytes[0] = (byte)(bytes[0] ^ 0x01);
                        relay.EnqueueMessage(inMsg with { OpaqueBytes = bytes });
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

            return true;
        }
        finally
        {
            _relayGate.Release();
        }
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

        await _relayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _relayGate.Release();
        }
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

        await _relayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _relayGate.Release();
        }
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

        await _relayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = GetRelayOrThrow(relayHostPeerId);
            var snapshot = relay.MessageQueue
                .Select(kvp => kvp.Value)
                .OfType<InboundRelayMessage>()
                .Where(x => x.TargetPkh.AsSpan().SequenceEqual(targetPkh))
                .OrderBy(x => x.EnqueuedUtc)
                .Take(max)
                .ToList();
            if (snapshot.Count == 0) return Array.Empty<InboundRelayMessage>();

            foreach (var msg in snapshot)
            {
                relay.RemoveMessage(msg.AckId);
            }

            return snapshot;
        }
        finally
        {
            _relayGate.Release();
        }
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

            await _relayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                _relayGate.Release();
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
        await _relayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _relayGate.Release();
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

        try
        {
            var hello = HandshakeInitiatorHello.Parser.ParseFrom(opaqueBytes);
            if (hello is not null
                && hello.HasInitiatorIdentityKeySpki && hello.InitiatorIdentityKeySpki.Length > 0
                && hello.HasInitiatorEphemeralKeySpki && hello.InitiatorEphemeralKeySpki.Length > 0
                && hello.HasSignedPreKeyId && hello.SignedPreKeyId.Length > 0)
            {
                _diagnostics.Emit(
                    SimulatorDiagnosticEventType.HandshakeStateTransition,
                    "Standard handshake hello received (relayed)",
                    peerId: simulatedPeerId,
                    contextTag: "HelloReceived");

                var req = new EstablishSessionRequest
                {
                    Version = 1,
                    IdentitySigningKey = hello.InitiatorIdentityKeySpki,
                    EphemeralKey = hello.InitiatorEphemeralKeySpki,
                    PrekeyId = hello.SignedPreKeyId
                };

                if (hello.HasOneTimePreKeyId && hello.OneTimePreKeyId.Length > 0)
                {
                    req.OnetimePrekeyId = hello.OneTimePreKeyId;
                }

                return await ReceiveEstablishSessionFromMainAsync(simulatedPeerId, req, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // ignore
        }

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
                await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                    _peerGate.Release();
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

            var models = await _store.LoadPeersAsync(cancellationToken).ConfigureAwait(false);
            var relationships = await _store.LoadRelationshipsAsync(cancellationToken).ConfigureAwait(false);

            await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _peers.Clear();
                _peerById.Clear();

                foreach (var model in models)
                {
                    _peers.Add(model);
                    _peerById[model.PeerId] = model;
                    AttachRuntimePersistence(model);
                }

                _relationships.Clear();
                foreach (var rel in relationships)
                {
                    _relationships.Add(rel);
                }
            }
            finally
            {
                _peerGate.Release();
            }

            await InitializeRelaysAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_initGate)
            {
                _initializeTask = null;
            }
        }
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
        var model = new SimulatedPeerModel(
            peerId: peerId,
            displayName: name,
            isOnline: true,
            isRelayCapable: false,
            identitySigningKeySpki: spki,
            identitySigningKeyPrivateKeyEcPrivateKey: priv,
            connectionMode: ConnectionMode.Direct,
            host: null,
            port: 0,
            relayPeerId: null);

        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _peerById[peerId] = model;
            AttachRuntimePersistence(model);
        }
        finally
        {
            _peerGate.Release();
        }

        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _peers.Add(model);
        }
        finally
        {
            _peerGate.Release();
        }

        _saveTrigger.OnNext(Unit.Default);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.PeerCreated,
            $"Peer created: {(string.IsNullOrWhiteSpace(model.DisplayName.CurrentValue) ? model.PeerId.ToString()[..8] : model.DisplayName.CurrentValue)}",
            peerId: peerId);

        return peerId;
    }

    public Task<Guid?> TryGetPeerIdByIdentityPkhAsync(byte[] recipientPublicKeyHash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (recipientPublicKeyHash is null) throw new ArgumentNullException(nameof(recipientPublicKeyHash));
        if (recipientPublicKeyHash.Length == 0) return Task.FromResult<Guid?>(null);

        List<Guid> matches;
        _peerGate.Wait(cancellationToken);
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
            _peerGate.Release();
        }

        if (matches.Count != 1) return Task.FromResult<Guid?>(null);
        return Task.FromResult<Guid?>(matches[0]);
    }

    public async Task RemovePeerAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string name;
        SimulatedPeerModel? removedModel = null;

        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _peerGate.Release();
        }

        if (removedModel is not null)
        {
            await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _ = _peers.Remove(removedModel);
            }
            finally
            {
                _peerGate.Release();
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

        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _peerGate.Release();
        }

        _saveTrigger.OnNext(Unit.Default);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.PreKeyPublishRelationshipAdded,
            $"Pre-keys relationship added: {publisherPeerId.ToString()[..8]} -> {hostPeerId.ToString()[..8]}",
            peerId: publisherPeerId);
    }

    public async Task RemovePublishedKeysRelationshipAsync(Guid publisherPeerId, Guid hostPeerId, CancellationToken cancellationToken = default)
    {
        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rel = new PeerRelationship(publisherPeerId, hostPeerId, RelationshipType.PublishedKey);
            if (!_relationships.Contains(rel)) return;

            _ = _relationships.Remove(rel);
        }
        finally
        {
            _peerGate.Release();
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

        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _peerGate.Release();
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

        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rel = new PeerRelationship(relayHostPeerId, peerId, RelationshipType.RelayActiveSession);
            if (!_relationships.Contains(rel)) return;

            _ = _relationships.Remove(rel);
        }
        finally
        {
            _peerGate.Release();
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

        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _peerGate.Release();
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

        await _peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _peerGate.Release();
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

        var loaded = await _store.LoadRelayAsync(peerId, cancellationToken).ConfigureAwait(false)
            ?? new SimulatedRelayModel(peerId);

        var added = false;
        await _relayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_relayByHostPeerId.ContainsKey(peerId)) return;

            _relayByHostPeerId[peerId] = loaded;
            AttachRelayPersistence(loaded);
            added = true;
        }
        finally
        {
            _relayGate.Release();
        }

        if (added)
        {
            await _relayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _relays.Add(loaded);
            }
            finally
            {
                _relayGate.Release();
            }
        }
    }

    private async Task RemoveRelayIfExistsAsync(Guid relayHostPeerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SimulatedRelayModel? relay = null;
        await _relayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_relayByHostPeerId.TryGetValue(relayHostPeerId, out relay)) return;

            _relayByHostPeerId.Remove(relayHostPeerId);

            if (_relayPersistenceByHostPeerId.Remove(relayHostPeerId, out var d))
            {
                d.Dispose();
            }
        }
        finally
        {
            _relayGate.Release();
        }

        await _relayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = _relays.Remove(relay);
        }
        finally
        {
            _relayGate.Release();
        }

        relay.Dispose();
    }

}
