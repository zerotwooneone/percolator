using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Cryptography;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorRelayDeliveryService
{
    Task DeliverToMainAsync(Guid relayHostPeerId, SessionId relayHostToMainSessionId, RelayQueuedBlobDto item, CancellationToken cancellationToken = default);

    Task DeliverToPeerAsync(Guid relayHostPeerId, Guid recipientPeerId, RelayQueuedBlobDto item, CancellationToken cancellationToken = default);
}

public sealed class SimulatorRelayDeliveryService : ISimulatorRelayDeliveryService
{
    private readonly PercolatorMessageService _messageService;
    private readonly ISimulatorStateService _state;
    private readonly ILogger<SimulatorRelayDeliveryService> _logger;

    public SimulatorRelayDeliveryService(
        PercolatorMessageService messageService,
        ISimulatorStateService state,
        ILogger<SimulatorRelayDeliveryService> logger)
    {
        _messageService = messageService;
        _state = state;
        _logger = logger;
    }

    public async Task DeliverToMainAsync(
        Guid relayHostPeerId,
        SessionId relayHostToMainSessionId,
        RelayQueuedBlobDto item,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (item is null) throw new ArgumentNullException(nameof(item));

        var env = new InternalEnvelope
        {
            RelayOpaqueEnvelope = new RelayOpaqueEnvelope
            {
                Version = 1,
                OpaquePayload = ByteString.CopyFrom(item.OpaqueBytes),
                MessageAckId = ByteString.CopyFrom(item.AckId.ToByteArray())
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
        RelayQueuedBlobDto item,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (item is null) throw new ArgumentNullException(nameof(item));

        var forwarded = await _state
            .ReceiveRelayedOpaquePayloadAsync(recipientPeerId, item.OpaqueBytes, cancellationToken)
            .ConfigureAwait(false);

        if (forwarded?.Response is null || !forwarded.Response.HasResponsePayload || forwarded.Response.ResponsePayload.Length == 0)
        {
            return;
        }

        // For standard handshake via relay: opaque payload is HandshakeInitiatorHello; route response back to initiator PKH.
        try
        {
            var hello = HandshakeInitiatorHello.Parser.ParseFrom(item.OpaqueBytes);
            if (hello is null || !hello.HasInitiatorIdentityKeySpki || hello.InitiatorIdentityKeySpki.Length == 0)
            {
                return;
            }

            var initiatorPkh = System.Security.Cryptography.SHA256.HashData(hello.InitiatorIdentityKeySpki.ToByteArray());

            await _state.EnqueueRelayOpaqueAsync(
                    relayHostPeerId: relayHostPeerId,
                    recipientRoutingKey: initiatorPkh,
                    opaqueBytes: forwarded.ToByteArray(),
                    debugType: nameof(EstablishSessionResponse),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[simulator] Failed to enqueue EstablishSessionResponse back to relay host {RelayHost}", relayHostPeerId);
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
