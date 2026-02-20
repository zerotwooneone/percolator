using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Sessions;
using Google.Protobuf;
using Grpc.Core;
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

    Task ReceiveInviteHandshakeResponseFromMainAsync(
        Guid simulatedPeerId,
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default);

    Task<DeliverOpaqueMessageResponse> ReceiveOpaqueMessageFromMainAsync(
        Guid simulatedPeerId,
        DeliverOpaqueMessageRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class SimulatedPeerRuntimeService : ISimulatedPeerRuntimeService
{
    private readonly ISimulatedPeerDirectory _peers;
    private readonly PercolatorMessageService _messageService;
    private readonly ConcurrentDictionary<Guid, SimulatedPeerRuntime> _runtimeByPeerId = new();

    public SimulatedPeerRuntimeService(ISimulatedPeerDirectory peers, PercolatorMessageService messageService)
    {
        _peers = peers;
        _messageService = messageService;
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
        return runtime.EncryptInternalEnvelopeAsync(sessionId, envelope, cancellationToken);
    }

    public async Task DeliverInviteHandshakeResponseToMainAsync(
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default)
    {
        if (response is null) throw new ArgumentNullException(nameof(response));

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:0",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: new Metadata(),
            cancellationToken: cancellationToken);

        await _messageService.DeliverInviteHandshakeResponse(response, ctx).ConfigureAwait(false);
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

        if (Guid.TryParse(response.RequestCorrelationId, out var corr))
        {
            model.MarkInboundPending(corr);
        }
        else
        {
            model.MarkInboundPending(Guid.NewGuid());
        }

        return Task.CompletedTask;
    }

    public Task<DeliverOpaqueMessageResponse> ReceiveOpaqueMessageFromMainAsync(
        Guid simulatedPeerId,
        DeliverOpaqueMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null) throw new ArgumentNullException(nameof(request));

        var model = _peers.Peers.FirstOrDefault(p => p.PeerId == simulatedPeerId)
            ?? throw new InvalidOperationException($"No simulated peer exists with id {simulatedPeerId}");

        // For C.D2 we only need to prove routing works; full peer-side decrypt/dispatch comes in later chunks.
        model.MarkInboundPending(Guid.NewGuid());

        return Task.FromResult(new DeliverOpaqueMessageResponse { Version = 1 });
    }

    private SimulatedPeerRuntime CreateRuntime(Guid simulatedPeerId)
    {
        var model = _peers.Peers.FirstOrDefault(p => p.PeerId == simulatedPeerId);
        if (model is null)
        {
            throw new InvalidOperationException($"No simulated peer exists with id {simulatedPeerId}");
        }

        return new SimulatedPeerRuntime(model);
    }

    private sealed class SimulatedPeerRuntime
    {
        private readonly SimulatedPeerModel _model;
        private readonly ISessionCrypto _crypto;
        private readonly IClock _clock;
        private readonly ConcurrentDictionary<Guid, SecureSession> _sessionsById = new();

        public SimulatedPeerRuntime(SimulatedPeerModel model)
        {
            _model = model;
            _crypto = new AeadSessionCrypto();
            _clock = new SystemClock();
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
    }

    private sealed class ServerCallContextStub : ServerCallContext
    {
        private readonly string _peer;
        private readonly DateTime _deadline;
        private readonly Metadata _requestHeaders;
        private readonly CancellationToken _cancellationToken;

        public ServerCallContextStub(string peer, DateTime deadline, Metadata requestHeaders, CancellationToken cancellationToken)
        {
            _peer = peer;
            _deadline = deadline;
            _requestHeaders = requestHeaders;
            _cancellationToken = cancellationToken;
        }

        protected override string MethodCore => "/percolator.contracts.TransportService/DeliverInviteHandshakeResponse";
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
