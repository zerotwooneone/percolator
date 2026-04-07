using System.Collections.Generic;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorRelayDeliveryService
{
    Task DeliverToMainAsync(
        Guid relayHostPeerId,
        SessionId relayHostToMainSessionId,
        Guid ackId,
        byte[] opaqueBytes,
        string? debugType,
        CancellationToken cancellationToken = default);

    Task DeliverToPeerAsync(
        Guid relayHostPeerId,
        Guid recipientPeerId,
        Guid ackId,
        byte[] opaqueBytes,
        string? debugType,
        CancellationToken cancellationToken = default);
}

public sealed class SimulatorRelayDeliveryService : ISimulatorRelayDeliveryService
{
    private readonly PercolatorMessageService _messageService;
    private readonly ISimulatorStateService _state;
    private readonly ISelfIdentityRepository _selfIdentityRepository;
    private readonly ISelfIdentityKeysStore _selfIdentityKeysStore;
    private readonly ILogger<SimulatorRelayDeliveryService> _logger;
    private readonly ISimulatorDiagnosticsService _diagnostics;

    public SimulatorRelayDeliveryService(
        PercolatorMessageService messageService,
        ISimulatorStateService state,
        ISelfIdentityRepository selfIdentityRepository,
        ISelfIdentityKeysStore selfIdentityKeysStore,
        ILogger<SimulatorRelayDeliveryService> logger,
        ISimulatorDiagnosticsService diagnostics)
    {
        _messageService = messageService;
        _state = state;
        _selfIdentityRepository = selfIdentityRepository;
        _selfIdentityKeysStore = selfIdentityKeysStore;
        _logger = logger;
        _diagnostics = diagnostics;
    }

    public async Task DeliverToMainAsync(
        Guid relayHostPeerId,
        SessionId relayHostToMainSessionId,
        Guid ackId,
        byte[] opaqueBytes,
        string? debugType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (opaqueBytes is null) throw new ArgumentNullException(nameof(opaqueBytes));

        var env = new InternalEnvelope
        {
            RelayOpaqueEnvelope = new RelayOpaqueEnvelope
            {
                Version = 1,
                OpaquePayload = ByteString.CopyFrom(opaqueBytes),
                MessageAckId = ByteString.CopyFrom(ackId.ToByteArray())
            }
        };

        var cipher = await _state
            .EncryptInternalEnvelopeAsync(relayHostPeerId, relayHostToMainSessionId, env, cancellationToken)
            .ConfigureAwait(false);

        var request = new DeliverOpaqueMessageRequest
        {
            Version = 1,
            Payload = ByteString.CopyFrom(cipher.Value)
        };

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:0",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: new Metadata(),
            cancellationToken: cancellationToken);

        var resp = await _messageService.DeliverOpaqueMessage(request, ctx).ConfigureAwait(false);

        var respCipher = new SessionRatchetMessage(resp.ResponsePayload.ResponsePayload.ToByteArray());
        var plain = await _state.DecryptSessionMessageAsync(relayHostPeerId, relayHostToMainSessionId, respCipher, cancellationToken).ConfigureAwait(false);
        var response = RelayOpaqueResponse.Parser.ParseFrom(plain.Value);
    }

    public async Task DeliverToPeerAsync(
        Guid relayHostPeerId,
        Guid recipientPeerId,
        Guid ackId,
        byte[] opaqueBytes,
        string? debugType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (opaqueBytes is null) throw new ArgumentNullException(nameof(opaqueBytes));

        var forwarded = await _state
            .ReceiveRelayedOpaquePayloadAsync(recipientPeerId, opaqueBytes, cancellationToken)
            .ConfigureAwait(false);

        if (forwarded?.Response is null || !forwarded.Response.HasResponsePayload || forwarded.Response.ResponsePayload.Length == 0)
        {
            return;
        }

        // For standard handshake via relay: opaque payload is HandshakeInitiatorHello; route response back to initiator PKH.
        try
        {
            var hello = HandshakeInitiatorHello.Parser.ParseFrom(opaqueBytes);
            if (hello is null || !hello.HasInitiatorIdentityKeySpki || hello.InitiatorIdentityKeySpki.Length == 0)
            {
                return;
            }

            var initiatorPkh = System.Security.Cryptography.SHA256.HashData(hello.InitiatorIdentityKeySpki.ToByteArray());

            var initiatorPeerId = await _state
                .TryGetPeerIdByIdentityPkhAsync(initiatorPkh, cancellationToken)
                .ConfigureAwait(false);

            if (initiatorPeerId.HasValue)
            {
                await _state.EnqueueRelayDownstreamToPeerAsync(
                        relayHostPeerId: relayHostPeerId,
                        targetPkh: initiatorPkh,
                        opaqueBytes: forwarded.ToByteArray(),
                        debugType: nameof(EstablishSessionResponse),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var matchesMainIdentity = await MatchesAnyMainIdentityPkhAsync(initiatorPkh, cancellationToken).ConfigureAwait(false);
                if (matchesMainIdentity)
                {
                    await _state.EnqueueRelayUpstreamToMainAsync(
                            relayHostPeerId: relayHostPeerId,
                            opaqueBytes: forwarded.ToByteArray(),
                            debugType: nameof(EstablishSessionResponse),
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    _diagnostics.Emit(
                        SimulatorDiagnosticEventType.RelayRoutingFailure,
                        $"Relay response routing failure (no simulated peer or main identity for PKH): {nameof(EstablishSessionResponse)}",
                        relayHostPeerId: relayHostPeerId,
                        ackId: ackId);
                    return;
                }
            }

            _diagnostics.Emit(
                SimulatorDiagnosticEventType.HandshakeStateTransition,
                "Standard handshake response enqueued (relayed)",
                peerId: recipientPeerId,
                relayHostPeerId: relayHostPeerId,
                contextTag: "ResponseEnqueued");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[simulator] Failed to enqueue EstablishSessionResponse back to relay host {RelayHost}", relayHostPeerId);
        }
    }

    private async Task<bool> MatchesAnyMainIdentityPkhAsync(byte[] pkh, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (pkh is null) throw new ArgumentNullException(nameof(pkh));
        if (pkh.Length == 0) return false;

        IReadOnlyList<Percolator.Identity.Model.SelfIdentity> identities;
        try
        {
            identities = await _selfIdentityRepository.ListAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }

        foreach (var identity in identities)
        {
            ct.ThrowIfCancellationRequested();

            X3dhKeys? keys;
            try
            {
                keys = await _selfIdentityKeysStore.LoadAsync(identity.Id, ct).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            if (keys is null)
            {
                continue;
            }

            try
            {
                var spki = keys.IdentitySigningKey.ExportSubjectPublicKeyInfo();
                var computed = System.Security.Cryptography.SHA256.HashData(spki);
                if (computed.AsSpan().SequenceEqual(pkh))
                {
                    return true;
                }
            }
            finally
            {
                try { keys.Dispose(); } catch { }
            }
        }

        return false;
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

        protected override string MethodCore => "/percolator.contracts.TransportService/DeliverOpaqueMessage";
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
