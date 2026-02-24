using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Sessions;
using Google.Protobuf;
using Grpc.Core;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Desktop.Wpf.Features.Simulator;

public sealed record SimulatedPeerInviteAcceptance(
    SessionId SessionId,
    InviteHandshakeResponse Response);

public interface ISimulatedPeerRuntimeService
{
    Task<SimulatedPeerInviteAcceptance> AcceptReverseSignalInviteAsync(
        Guid simulatedPeerId,
        Guid inviterPeerId,
        EstablishDirectSessionRequest invite,
        CancellationToken cancellationToken = default);

    Task<SessionRatchetMessage> EncryptInternalEnvelopeAsync(
        Guid simulatedPeerId,
        SessionId sessionId,
        InternalEnvelope envelope,
        CancellationToken cancellationToken = default);

    Task DeliverInviteHandshakeResponseToMainAsync(
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default);

    Task<EstablishSessionResponse> DeliverEstablishSessionToMainAsync(
        EstablishSessionRequest request,
        CancellationToken cancellationToken = default);

    Task ReceiveInviteHandshakeResponseFromMainAsync(
        Guid simulatedPeerId,
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default);

    Task<DeliverOpaqueMessageResponse> ReceiveOpaqueMessageFromMainAsync(
        Guid simulatedPeerId,
        DeliverOpaqueMessageRequest request,
        CancellationToken cancellationToken = default);

    Task<EstablishSessionResponse?> ReceiveRelayedOpaquePayloadAsync(
        Guid simulatedPeerId,
        byte[] opaqueBytes,
        CancellationToken cancellationToken = default);

    Task<EstablishSessionResponse> ReceiveEstablishSessionFromMainAsync(
        Guid simulatedPeerId,
        EstablishSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<Plaintext> DecryptSessionMessageAsync(
        Guid simulatedPeerId,
        SessionId sessionId,
        SessionRatchetMessage message,
        CancellationToken cancellationToken = default);

    void RecordOutboundInviteSignedPreKeyPrivate(
        Guid simulatedPeerId,
        Guid requestCorrelationId,
        byte[] signedPreKeyPrivateEcPrivateKey);

    Task<SessionId?> TryFinalizeInviteHandshakeResponseFromMainAsync(
        Guid simulatedPeerId,
        Guid acceptorPeerId,
        Guid requestCorrelationId,
        CancellationToken cancellationToken = default);
}

public sealed class SimulatedPeerRuntimeService : ISimulatedPeerRuntimeService
{
    private readonly ISimulatedPeerDirectory _peers;
    private readonly PercolatorMessageService _messageService;
    private readonly ISimulatorStateService _state;
    private readonly ISimulatedPeerPendingInbox _pending;
    private readonly ConcurrentDictionary<Guid, SimulatedPeerRuntime> _runtimeByPeerId = new();

    public SimulatedPeerRuntimeService(
        ISimulatedPeerDirectory peers,
        PercolatorMessageService messageService,
        ISimulatorStateService state,
        ISimulatedPeerPendingInbox pending)
    {
        _peers = peers;
        _messageService = messageService;
        _state = state;
        _pending = pending;
    }

    public Task<SimulatedPeerInviteAcceptance> AcceptReverseSignalInviteAsync(
        Guid simulatedPeerId,
        Guid inviterPeerId,
        EstablishDirectSessionRequest invite,
        CancellationToken cancellationToken = default)
    {
        if (invite is null) throw new ArgumentNullException(nameof(invite));

        var runtime = _runtimeByPeerId.GetOrAdd(simulatedPeerId, CreateRuntime);
        return runtime.AcceptReverseSignalInviteAsync(inviterPeerId, invite, cancellationToken);
    }

    public Task<SessionRatchetMessage> EncryptInternalEnvelopeAsync(
        Guid simulatedPeerId,
        SessionId sessionId,
        InternalEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));
        var runtime = _runtimeByPeerId.GetOrAdd(simulatedPeerId, CreateRuntime);
        return EncryptInternalEnvelopeInnerAsync(simulatedPeerId, runtime, sessionId, envelope, cancellationToken);
    }

    private async Task<SessionRatchetMessage> EncryptInternalEnvelopeInnerAsync(
        Guid simulatedPeerId,
        SimulatedPeerRuntime runtime,
        SessionId sessionId,
        InternalEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var msg = await runtime.EncryptInternalEnvelopeAsync(sessionId, envelope, cancellationToken).ConfigureAwait(false);
        await PersistRuntimeStoreAsync(simulatedPeerId, runtime, cancellationToken).ConfigureAwait(false);
        return msg;
    }

    public async Task DeliverInviteHandshakeResponseToMainAsync(
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default)
    {
        if (response is null) throw new ArgumentNullException(nameof(response));

        var ctx = new ServerCallContextStub(
            method: "/percolator.contracts.TransportService/DeliverInviteHandshakeResponse",
            peer: "ipv4:127.0.0.1:0",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: new Metadata(),
            cancellationToken: cancellationToken);

        await _messageService.DeliverInviteHandshakeResponse(response, ctx).ConfigureAwait(false);
    }

    public Task<EstablishSessionResponse> DeliverEstablishSessionToMainAsync(
        EstablishSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null) throw new ArgumentNullException(nameof(request));

        var ctx = new ServerCallContextStub(
            method: "/percolator.contracts.TransportService/EstablishSession",
            peer: "ipv4:127.0.0.1:0",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: new Metadata(),
            cancellationToken: cancellationToken);

        return _messageService.EstablishSession(request, ctx);
    }

    public Task ReceiveInviteHandshakeResponseFromMainAsync(
        Guid simulatedPeerId,
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (response is null) throw new ArgumentNullException(nameof(response));

        var model = _peers.Peers.FirstOrDefault(p => p.PeerId == simulatedPeerId)
            ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedPeerId}");

        var corr = Guid.TryParse(response.RequestCorrelationId, out var parsed)
            ? parsed
            : Guid.NewGuid();

        _pending.AddInviteHandshakeResponse(simulatedPeerId, corr, response);
        model.MarkInboundPending(corr);

        return Task.CompletedTask;
    }

    public Task<DeliverOpaqueMessageResponse> ReceiveOpaqueMessageFromMainAsync(
        Guid simulatedPeerId,
        DeliverOpaqueMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null) throw new ArgumentNullException(nameof(request));

        var runtime = _runtimeByPeerId.GetOrAdd(simulatedPeerId, CreateRuntime);
        return runtime.ReceiveOpaqueMessageFromMainAsync(simulatedPeerId, request, _state, cancellationToken);
    }

    public async Task<EstablishSessionResponse?> ReceiveRelayedOpaquePayloadAsync(
        Guid simulatedPeerId,
        byte[] opaqueBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (opaqueBytes is null) throw new ArgumentNullException(nameof(opaqueBytes));
        if (opaqueBytes.Length == 0) return null;

        var runtime = _runtimeByPeerId.GetOrAdd(simulatedPeerId, CreateRuntime);
        var resp = await runtime.TryHandleRelayedOpaquePayloadAsync(opaqueBytes, cancellationToken).ConfigureAwait(false);
        if (resp is not null)
        {
            await PersistRuntimeStoreAsync(simulatedPeerId, runtime, cancellationToken).ConfigureAwait(false);
        }
        return resp;
    }

    public Task<EstablishSessionResponse> ReceiveEstablishSessionFromMainAsync(
        Guid simulatedPeerId,
        EstablishSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null) throw new ArgumentNullException(nameof(request));

        var runtime = _runtimeByPeerId.GetOrAdd(simulatedPeerId, CreateRuntime);
        return ReceiveEstablishSessionFromMainInnerAsync(simulatedPeerId, runtime, request, cancellationToken);
    }

    private async Task<EstablishSessionResponse> ReceiveEstablishSessionFromMainInnerAsync(
        Guid simulatedPeerId,
        SimulatedPeerRuntime runtime,
        EstablishSessionRequest request,
        CancellationToken cancellationToken)
    {
        var resp = await runtime.ReceiveEstablishSessionFromMainAsync(request, cancellationToken).ConfigureAwait(false);
        await PersistRuntimeStoreAsync(simulatedPeerId, runtime, cancellationToken).ConfigureAwait(false);
        return resp;
    }

    public Task<Plaintext> DecryptSessionMessageAsync(
        Guid simulatedPeerId,
        SessionId sessionId,
        SessionRatchetMessage message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message is null) throw new ArgumentNullException(nameof(message));

        var runtime = _runtimeByPeerId.GetOrAdd(simulatedPeerId, CreateRuntime);
        return DecryptSessionMessageInnerAsync(simulatedPeerId, runtime, sessionId, message, cancellationToken);
    }

    private async Task<Plaintext> DecryptSessionMessageInnerAsync(
        Guid simulatedPeerId,
        SimulatedPeerRuntime runtime,
        SessionId sessionId,
        SessionRatchetMessage message,
        CancellationToken cancellationToken)
    {
        var pt = await runtime.DecryptSessionMessageAsync(sessionId, message, cancellationToken).ConfigureAwait(false);
        await PersistRuntimeStoreAsync(simulatedPeerId, runtime, cancellationToken).ConfigureAwait(false);
        return pt;
    }

    public void RecordOutboundInviteSignedPreKeyPrivate(
        Guid simulatedPeerId,
        Guid requestCorrelationId,
        byte[] signedPreKeyPrivateEcPrivateKey)
    {
        if (signedPreKeyPrivateEcPrivateKey is null) throw new ArgumentNullException(nameof(signedPreKeyPrivateEcPrivateKey));
        var runtime = _runtimeByPeerId.GetOrAdd(simulatedPeerId, CreateRuntime);
        runtime.RecordOutboundInviteSignedPreKeyPrivate(requestCorrelationId, signedPreKeyPrivateEcPrivateKey);
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

        var runtime = _runtimeByPeerId.GetOrAdd(simulatedPeerId, CreateRuntime);
        var sessionId = await runtime.TryFinalizeInviteHandshakeResponseAsync(
                acceptorPeerId,
                requestCorrelationId,
                response,
                cancellationToken)
            .ConfigureAwait(false);

        if (sessionId is null)
        {
            return null;
        }

        await PersistRuntimeStoreAsync(simulatedPeerId, runtime, cancellationToken).ConfigureAwait(false);

        _ = _pending.TryTakeInviteHandshakeResponse(simulatedPeerId, requestCorrelationId, out _);
        return sessionId;
    }

    private async Task PersistRuntimeStoreAsync(Guid simulatedPeerId, SimulatedPeerRuntime runtime, CancellationToken cancellationToken)
    {
        var store = runtime.ExportRuntimeStore();
        await _state.SaveRuntimeStoreAsync(simulatedPeerId, store, cancellationToken).ConfigureAwait(false);
    }

    private SimulatedPeerRuntime CreateRuntime(Guid simulatedPeerId)
    {
        var model = _peers.Peers.FirstOrDefault(p => p.PeerId == simulatedPeerId);
        if (model is null)
        {
            throw new InvalidOperationException($"No simulated peer exists with id {simulatedPeerId}");
        }

        var runtime = new SimulatedPeerRuntime(model);
        var existing = _state.TryGetRuntimeStoreAsync(simulatedPeerId).GetAwaiter().GetResult();
        if (existing is not null)
        {
            runtime.ImportRuntimeStore(existing);
        }
        return runtime;
    }

    private sealed class SimulatedPeerRuntime
    {
        private readonly SimulatedPeerModel _model;
        private readonly ISessionCrypto _crypto;
        private readonly IClock _clock;
        private readonly ConcurrentDictionary<Guid, SecureSession> _sessionsById = new();
        private readonly ConcurrentDictionary<Guid, byte[]> _signedPreKeyPrivateByCorrelation = new();
        private readonly ConcurrentDictionary<Guid, (byte[] spkPriv, byte[] spkSpki)> _signedPreKeyById = new();

        public SimulatedPeerRuntime(SimulatedPeerModel model)
        {
            _model = model;
            _crypto = new AeadSessionCrypto();
            _clock = new SystemClock();
        }

        public void RecordOutboundInviteSignedPreKeyPrivate(Guid requestCorrelationId, byte[] signedPreKeyPrivateEcPrivateKey)
        {
            _signedPreKeyPrivateByCorrelation[requestCorrelationId] = signedPreKeyPrivateEcPrivateKey;
        }

        public Task<SessionId?> TryFinalizeInviteHandshakeResponseAsync(
            Guid acceptorPeerId,
            Guid requestCorrelationId,
            InviteHandshakeResponse response,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (response is null) throw new ArgumentNullException(nameof(response));

            if (!_signedPreKeyPrivateByCorrelation.TryGetValue(requestCorrelationId, out var spkPrivBytes))
            {
                return Task.FromResult<SessionId?>(null);
            }

            if (!response.HasAcceptorIdentityKey || response.AcceptorIdentityKey.Length == 0)
                throw new InvalidOperationException("InviteHandshakeResponse missing acceptor_identity_key");
            if (!response.HasAcceptorX3DhEphemeralKey || response.AcceptorX3DhEphemeralKey.Length == 0)
                throw new InvalidOperationException("InviteHandshakeResponse missing acceptor_x3dh_ephemeral_key");
            if (!response.HasInitialRatchetMessage || response.InitialRatchetMessage.Length == 0)
                throw new InvalidOperationException("InviteHandshakeResponse missing initial_ratchet_message");

            var acceptorIdentityPublic = new RatchetIdentityKey(response.AcceptorIdentityKey.ToByteArray());
            var acceptorEphemeralPublic = new RatchetEphemeralKey(response.AcceptorX3DhEphemeralKey.ToByteArray());

            var localIkPriv = new PrivatePreKey(_model.IdentitySigningKeyPrivateKeyEcPrivateKey);
            var localSpkPriv = new PrivatePreKey(spkPrivBytes);

            SharedSecret shared;
            try
            {
                shared = _crypto.X3DH_Respond(
                    acceptorIdentityPublic,
                    acceptorEphemeralPublic,
                    localIkPriv,
                    localSpkPriv,
                    localOtkPrivate: null);
            }
            catch
            {
                return Task.FromResult<SessionId?>(null);
            }

            var root = new RootKey(shared.Value);

            SessionRatchetMessage ratchetMessage;
            try
            {
                ratchetMessage = new SessionRatchetMessage(response.InitialRatchetMessage.ToByteArray());
            }
            catch
            {
                return Task.FromResult<SessionId?>(null);
            }

            var tmp = RatchetBootstrap.CreateResponderSession(
                SessionId.NewId(),
                new PeerId(acceptorPeerId),
                new ProtocolVersion(1),
                root,
                _clock);

            Plaintext pt;
            try
            {
                pt = tmp.Decrypt(ratchetMessage, _clock);
            }
            catch
            {
                return Task.FromResult<SessionId?>(null);
            }

            ResponderInnerHello inner;
            try
            {
                inner = ResponderInnerHello.Parser.ParseFrom(pt.Value);
            }
            catch
            {
                return Task.FromResult<SessionId?>(null);
            }

            if (!inner.HasVersion || inner.Version != 1) return Task.FromResult<SessionId?>(null);
            if (!inner.HasDirectSessionId || string.IsNullOrWhiteSpace(inner.DirectSessionId)) return Task.FromResult<SessionId?>(null);

            SessionId sid;
            try
            {
                sid = new SessionId(Guid.Parse(inner.DirectSessionId));
            }
            catch
            {
                return Task.FromResult<SessionId?>(null);
            }

            var final = SecureSession.Create(
                sid,
                tmp.RemotePeerId,
                tmp.ProtocolVersion,
                tmp.State,
                new AeadSessionCrypto(),
                _clock);

            _sessionsById[sid.Value] = final;
            return Task.FromResult<SessionId?>(sid);
        }

        public void ImportRuntimeStore(SimulatedPeerRuntimeStoreDto store)
        {
            if (store is null) throw new ArgumentNullException(nameof(store));

            _sessionsById.Clear();
            foreach (var s in store.Sessions ?? Enumerable.Empty<SimulatedSecureSessionDto>())
            {
                if (s.SessionId == Guid.Empty) continue;

                var id = new SessionId(s.SessionId);
                var remote = new PeerId(s.RemotePeerId == Guid.Empty ? Guid.NewGuid() : s.RemotePeerId);
                var ver = new ProtocolVersion(s.ProtocolVersion <= 0 ? 1 : s.ProtocolVersion);

                var state = new RatchetState(
                    new RootKey(s.RootKey ?? Array.Empty<byte>()),
                    s.SendChainKey is null ? null : new ChainKey(s.SendChainKey),
                    s.SendCounter,
                    s.RecvChainKey is null ? null : new ChainKey(s.RecvChainKey),
                    s.RecvCounter,
                    s.PrevChainLength,
                    s.RemoteRatchetKey is null ? null : new RatchetEphemeralKey(s.RemoteRatchetKey),
                    s.DhRatchetPrivateKey is null ? null : new PrivateEphemeralKey(s.DhRatchetPrivateKey),
                    1000);

                var session = SecureSession.Create(id, remote, ver, state, _crypto, _clock);
                _sessionsById[id.Value] = session;
            }

            _signedPreKeyById.Clear();
            foreach (var k in store.SignedPreKeys ?? Enumerable.Empty<SimulatedSignedPreKeyDto>())
            {
                if (k.SignedPreKeyId == Guid.Empty) continue;
                if (k.PrivateEcPrivateKey is null || k.PrivateEcPrivateKey.Length == 0) continue;
                if (k.PublicSpki is null || k.PublicSpki.Length == 0) continue;
                _signedPreKeyById[k.SignedPreKeyId] = (k.PrivateEcPrivateKey, k.PublicSpki);
            }
        }

        public SimulatedPeerRuntimeStoreDto ExportRuntimeStore()
        {
            var dto = new SimulatedPeerRuntimeStoreDto { Version = 1 };

            foreach (var kvp in _sessionsById)
            {
                var s = kvp.Value;
                dto.Sessions.Add(new SimulatedSecureSessionDto
                {
                    SessionId = s.Id.Value,
                    RemotePeerId = s.RemotePeerId.Value,
                    ProtocolVersion = s.ProtocolVersion.Value,
                    RootKey = s.State.RootKey.Value,
                    SendChainKey = s.State.SendingChainKey?.Value,
                    SendCounter = s.State.SendingCounter,
                    RecvChainKey = s.State.ReceivingChainKey?.Value,
                    RecvCounter = s.State.ReceivingCounter,
                    PrevChainLength = s.State.PreviousChainLength,
                    RemoteRatchetKey = s.State.RemoteRatchetKey?.Value,
                    DhRatchetPrivateKey = s.State.DhRatchetPrivateKey?.Value,
                    CreatedAtUtc = s.CreatedAtUtc,
                    LastUsedAtUtc = s.LastUsedAtUtc
                });
            }

            foreach (var kvp in _signedPreKeyById)
            {
                dto.SignedPreKeys.Add(new SimulatedSignedPreKeyDto
                {
                    SignedPreKeyId = kvp.Key,
                    PrivateEcPrivateKey = kvp.Value.spkPriv,
                    PublicSpki = kvp.Value.spkSpki
                });
            }

            return dto;
        }

        public Task<EstablishSessionResponse> ReceiveEstablishSessionFromMainAsync(
            EstablishSessionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request is null) throw new ArgumentNullException(nameof(request));

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

            var spk = _signedPreKeyById.GetOrAdd(spkId, id =>
            {
                using var identityEcdh = ECDiffieHellman.Create();
                identityEcdh.ImportECPrivateKey(_model.IdentitySigningKeyPrivateKeyEcPrivateKey, out _);
                var curve = identityEcdh.ExportParameters(false).Curve;
                using var signedPreKey = ECDiffieHellman.Create(curve);
                var spkSpki = signedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
                var spkPriv = signedPreKey.ExportECPrivateKey();
                return (spkPriv, spkSpki);
            });

            var initiatorId = new RatchetIdentityKey(request.IdentitySigningKey.ToByteArray());
            var initiatorEph = new RatchetEphemeralKey(request.EphemeralKey.ToByteArray());

            var localIkPriv = new PrivatePreKey(_model.IdentitySigningKeyPrivateKeyEcPrivateKey);
            var localSpkPriv = new PrivatePreKey(spk.spkPriv);
            PrivatePreKey? localOtkPriv = null;
            if (request.HasOnetimePrekeyId && request.OnetimePrekeyId.Length > 0)
            {
                // Simulator doesn't model a real OTK store yet for standard flow.
                localOtkPriv = null;
            }

            var shared = _crypto.X3DH_Respond(
                initiatorId,
                initiatorEph,
                localIkPriv,
                localSpkPriv,
                localOtkPriv);

            var sessionId = SessionId.NewId();
            var root = new RootKey(shared.Value);
            var session = RatchetBootstrap.CreateResponderSession(
                sessionId,
                Percolator.Cryptography.Primitives.PeerId.NewId(),
                new ProtocolVersion(1),
                root,
                _clock,
                crypto: _crypto);

            _sessionsById[sessionId.Value] = session;

            var responsePayload = new EstablishSessionResponse.Types.Response.Types.ResponsePayload
            {
                Version = 1,
                EphemeralKey = ByteString.CopyFrom(spk.spkSpki),
                SessionId = sessionId.Value.ToString()
            };

            var payloadBytes = responsePayload.ToByteArray();

            using var identityEcdh2 = ECDiffieHellman.Create();
            identityEcdh2.ImportECPrivateKey(_model.IdentitySigningKeyPrivateKeyEcPrivateKey, out _);
            using var identityEcdsa = ECDsa.Create(identityEcdh2.ExportParameters(true));
            var sig = identityEcdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);

            return Task.FromResult(new EstablishSessionResponse
            {
                Version = 1,
                Response = new EstablishSessionResponse.Types.Response
                {
                    Version = 1,
                    IdentitySigningKey = ByteString.CopyFrom(_model.IdentitySigningKeySpki),
                    ResponsePayload = ByteString.CopyFrom(payloadBytes),
                    PayloadSignature = ByteString.CopyFrom(sig)
                }
            });
        }

        public Task<SimulatedPeerInviteAcceptance> AcceptReverseSignalInviteAsync(
            Guid inviterPeerId,
            EstablishDirectSessionRequest invite,
            CancellationToken cancellationToken)
        {
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
                new RatchetIdentityKey(invite.InviterIdentityKey.ToByteArray()),
                signedPreKeyId: Guid.Empty,
                new PreKey(payload.InviterPreKey.InviterSignedPreKey.ToByteArray()),
                new Signature(payload.InviterPreKey.PreKeySignature.ToByteArray()),
                oneTimePreKeyId: null,
                oneTimePreKey: inviterOtk, 
                expirationDateUtc: payload.ExpiresAtUtc?.ToDateTimeOffset());

            var localIkPriv = new PrivatePreKey(_model.IdentitySigningKeyPrivateKeyEcPrivateKey);

            var x3 = _crypto.X3DH_Initiate(localIkPriv, inviterBundle);

            var sessionId = SessionId.NewId();
            var root = new RootKey(x3.SharedSecret.Value);
            var session = RatchetBootstrap.CreateInitiatorSession(
                sessionId,
                new PeerId(inviterPeerId),
                new ProtocolVersion(1),
                root,
                _clock,
                crypto: _crypto);

            _sessionsById[sessionId.Value] = session;

            var inner = new ResponderInnerHello
            {
                Version = 1,
                DirectSessionId = sessionId.Value.ToString()
            };

            var initial = session.Encrypt(new Plaintext(inner.ToByteArray()), _clock);

            var response = new InviteHandshakeResponse
            {
                Version = 1,
                RequestCorrelationId = payload.RequestCorrelationId,
                AcceptorIdentityKey = ByteString.CopyFrom(_model.IdentitySigningKeySpki),
                AcceptorX3DhEphemeralKey = ByteString.CopyFrom(x3.EphemeralPublic.Value),
                InitialRatchetMessage = ByteString.CopyFrom(initial.Value)
            };

            return Task.FromResult(new SimulatedPeerInviteAcceptance(sessionId, response));
        }

        public Task<SessionRatchetMessage> EncryptInternalEnvelopeAsync(
            SessionId sessionId,
            InternalEnvelope envelope,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_sessionsById.TryGetValue(sessionId.Value, out var session))
            {
                throw new InvalidOperationException($"No session exists for simulated peer {_model.PeerId} with id {sessionId.Value}");
            }

            var plaintext = new Plaintext(envelope.ToByteArray());
            var cipher = session.Encrypt(plaintext, _clock);
            _sessionsById[sessionId.Value] = session;
            return Task.FromResult(cipher);
        }

        public Task<Plaintext> DecryptSessionMessageAsync(
            SessionId sessionId,
            SessionRatchetMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_sessionsById.TryGetValue(sessionId.Value, out var session))
            {
                throw new InvalidOperationException($"No session exists for simulated peer {_model.PeerId} with id {sessionId.Value}");
            }

            var pt = session.Decrypt(message, _clock);
            _sessionsById[sessionId.Value] = session;
            return Task.FromResult(pt);
        }

        public async Task<DeliverOpaqueMessageResponse> ReceiveOpaqueMessageFromMainAsync(
            Guid simulatedPeerId,
            DeliverOpaqueMessageRequest request,
            ISimulatorStateService state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state is null) throw new ArgumentNullException(nameof(state));

            // Best-effort: try to decrypt with any known session (typically 1 per peer in simulator today)
            var cipher = new SessionRatchetMessage(request.Payload.ToByteArray());

            Plaintext? pt = null;
            SecureSession? matched = null;
            foreach (var kv in _sessionsById)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // SecureSession.Decrypt mutates state; only commit if it looks like a real plaintext.
                    var candidate = kv.Value;
                    var candidatePt = candidate.Decrypt(cipher, _clock);
                    if (candidatePt.Value.Length == 0)
                    {
                        continue;
                    }

                    pt = candidatePt;
                    matched = candidate;
                    _sessionsById[kv.Key] = candidate;
                    break;
                }
                catch
                {
                    // not this session
                }
            }

            if (pt is null || matched is null)
            {
                _model.MarkInboundPending(Guid.NewGuid());
                return new DeliverOpaqueMessageResponse { Version = 1 };
            }

            InternalEnvelope env;
            try
            {
                env = InternalEnvelope.Parser.ParseFrom(pt.Value);
            }
            catch
            {
                _model.MarkInboundPending(Guid.NewGuid());
                return new DeliverOpaqueMessageResponse { Version = 1 };
            }

            if (env.ApplicationPayloadCase == InternalEnvelope.ApplicationPayloadOneofCase.MessageQueueEnvelope
                && env.MessageQueueEnvelope?.MessageCase == MessageQueueEnvelope.MessageOneofCase.EnqueueOpaqueMessageRequest)
            {
                var enqueue = env.MessageQueueEnvelope.EnqueueOpaqueMessageRequest;
                if (!enqueue.HasRecipientPublicKeyHash || enqueue.RecipientPublicKeyHash.Length == 0)
                    throw new InvalidOperationException("EnqueueOpaqueMessageRequest missing recipient_public_key_hash");
                if (!enqueue.HasMessageBlob || enqueue.MessageBlob.Length == 0)
                    throw new InvalidOperationException("EnqueueOpaqueMessageRequest missing message_blob");

                await state.EnqueueRelayOpaqueAsync(
                    relayHostPeerId: simulatedPeerId,
                    recipientRoutingKey: enqueue.RecipientPublicKeyHash.ToByteArray(),
                    opaqueBytes: enqueue.MessageBlob.ToByteArray(),
                    debugType: "Opaque",
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return new DeliverOpaqueMessageResponse { Version = 1 };
            }

            _model.MarkInboundPending(Guid.NewGuid());
            return new DeliverOpaqueMessageResponse { Version = 1 };
        }

        public async Task<EstablishSessionResponse?> TryHandleRelayedOpaquePayloadAsync(
            byte[] opaqueBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var hello = HandshakeInitiatorHello.Parser.ParseFrom(opaqueBytes);
                if (hello is not null
                    && hello.HasInitiatorIdentityKeySpki && hello.InitiatorIdentityKeySpki.Length > 0
                    && hello.HasInitiatorEphemeralKeySpki && hello.InitiatorEphemeralKeySpki.Length > 0
                    && hello.HasSignedPreKeyId && hello.SignedPreKeyId.Length > 0)
                {
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

                    var resp = await ReceiveEstablishSessionFromMainAsync(req, cancellationToken).ConfigureAwait(false);
                    return resp;
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }
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
}
