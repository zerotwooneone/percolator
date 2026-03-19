namespace Desktop.Wpf.Features.Simulator;

public sealed record SimulatedRelayItem(
    Guid RelayHostPeerId,
    Guid RecipientPeerId,
    Guid AckId,
    byte[] OpaqueBytes,
    DateTimeOffset EnqueuedUtc,
    string? DebugType);

public interface ISimulatorRelayEmulator
{
    Task EnqueueToRelayHostAsync(Guid relayHostPeerId, Guid recipientPeerId, byte[] opaqueBytes, string? debugType = null, CancellationToken cancellationToken = default);

    Task EnqueueToRelayHostByRoutingKeyAsync(Guid relayHostPeerId, byte[] recipientRoutingKey, byte[] opaqueBytes, string? debugType = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SimulatedRelayItem>> FetchFromRelayHostAsync(Guid relayHostPeerId, Guid recipientPeerId, int max, CancellationToken cancellationToken = default);

    Task<bool> DeleteByAckIdAsync(Guid relayHostPeerId, Guid recipientPeerId, Guid ackId, CancellationToken cancellationToken = default);

    Task<int> ForwardQueuedToMainAsync(
        Guid relayHostPeerId,
        Guid recipientPeerId,
        Percolator.Cryptography.SessionId relayHostToMainSessionId,
        CancellationToken cancellationToken = default);

    Task<int> ForwardQueuedToMainByRoutingKeyAsync(
        Guid relayHostPeerId,
        byte[] recipientRoutingKey,
        Percolator.Cryptography.SessionId relayHostToMainSessionId,
        CancellationToken cancellationToken = default);

    Task<int> ForwardQueuedToPeerAsync(
        Guid relayHostPeerId,
        Guid recipientPeerId,
        int max,
        CancellationToken cancellationToken = default);
}

public sealed class SimulatorRelayEmulator : ISimulatorRelayEmulator
{
    private readonly ISimulatorStateService _state;

    private readonly ISimulatedPeerRuntimeService _peerRuntime;
    private readonly Percolator.Application.Network.PercolatorMessageService _messageService;

    private static readonly Guid MainNodeSentinelPeerId = new("88880000-0000-0000-0000-000000000000");

    public SimulatorRelayEmulator(
        ISimulatorStateService state,
        ISimulatedPeerRuntimeService peerRuntime,
        Percolator.Application.Network.PercolatorMessageService messageService)
    {
        _state = state;
        _peerRuntime = peerRuntime;
        _messageService = messageService;
    }

    public Task EnqueueToRelayHostAsync(
        Guid relayHostPeerId,
        Guid recipientPeerId,
        byte[] opaqueBytes,
        string? debugType = null,
        CancellationToken cancellationToken = default)
    {
        if (opaqueBytes is null) throw new ArgumentNullException(nameof(opaqueBytes));

        // Canonical simulator relay queue addressing is PKH (32 bytes). This method is legacy.
        // Use the simulated peer's identity PKH as the routing key.
        return EnqueueToRelayHostLegacyAsync(relayHostPeerId, recipientPeerId, opaqueBytes, debugType, cancellationToken);
    }

    private async Task EnqueueToRelayHostLegacyAsync(
        Guid relayHostPeerId,
        Guid recipientPeerId,
        byte[] opaqueBytes,
        string? debugType,
        CancellationToken cancellationToken)
    {
        // Avoid silently enqueuing to "Main" by Guid; PKH routing is required for main.
        if (recipientPeerId == new Guid("88880000-0000-0000-0000-000000000000"))
        {
            throw new InvalidOperationException("Legacy relay enqueue to Main by Guid is not supported; enqueue by routing key (PKH) instead.");
        }

        var pkh = await _peerRuntime.ComputePublicKeyHashAsync(recipientPeerId, cancellationToken).ConfigureAwait(false);
        await _state.EnqueueRelayOpaqueAsync(relayHostPeerId, pkh, opaqueBytes, debugType, cancellationToken).ConfigureAwait(false);
    }

    public Task EnqueueToRelayHostByRoutingKeyAsync(
        Guid relayHostPeerId,
        byte[] recipientRoutingKey,
        byte[] opaqueBytes,
        string? debugType = null,
        CancellationToken cancellationToken = default)
    {
        if (recipientRoutingKey is null) throw new ArgumentNullException(nameof(recipientRoutingKey));
        if (opaqueBytes is null) throw new ArgumentNullException(nameof(opaqueBytes));
        return _state.EnqueueRelayOpaqueAsync(relayHostPeerId, recipientRoutingKey, opaqueBytes, debugType, cancellationToken);
    }

    public async Task<IReadOnlyList<SimulatedRelayItem>> FetchFromRelayHostAsync(
        Guid relayHostPeerId,
        Guid recipientPeerId,
        int max,
        CancellationToken cancellationToken = default)
    {
        if (recipientPeerId == MainNodeSentinelPeerId)
        {
            throw new InvalidOperationException("FetchFromRelayHostAsync cannot target Main by Guid. Use Fetch/forward by routing key (PKH) instead.");
        }

        var routingKey = await _peerRuntime.ComputePublicKeyHashAsync(recipientPeerId, cancellationToken).ConfigureAwait(false);
        var dequeued = await _state.DequeueRelayOpaqueAsync(relayHostPeerId, routingKey, max, cancellationToken).ConfigureAwait(false);
        if (dequeued.Count == 0) return Array.Empty<SimulatedRelayItem>();

        return dequeued
            .Select(d => new SimulatedRelayItem(
                RelayHostPeerId: relayHostPeerId,
                RecipientPeerId: recipientPeerId,
                AckId: d.AckId,
                OpaqueBytes: d.OpaqueBytes,
                EnqueuedUtc: d.EnqueuedUtc,
                DebugType: d.DebugType))
            .ToList();
    }

    public Task<bool> DeleteByAckIdAsync(
        Guid relayHostPeerId,
        Guid recipientPeerId,
        Guid ackId,
        CancellationToken cancellationToken = default)
    {
        return _state.DeleteRelayOpaqueByAckIdAsync(relayHostPeerId, ackId, cancellationToken);
    }

    public async Task<int> ForwardQueuedToMainAsync(
        Guid relayHostPeerId,
        Guid recipientPeerId,
        Percolator.Cryptography.SessionId relayHostToMainSessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (recipientPeerId == MainNodeSentinelPeerId)
        {
            throw new InvalidOperationException("ForwardQueuedToMainAsync cannot target Main by Guid. Use ForwardQueuedToMainByRoutingKeyAsync (PKH) instead.");
        }

        var forwarded = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var routingKey = await _peerRuntime.ComputePublicKeyHashAsync(recipientPeerId, cancellationToken).ConfigureAwait(false);
            var item = await _state.PeekRelayOpaqueAsync(relayHostPeerId, routingKey, cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                break;
            }
            var env = new Percolator.Contracts.InternalEnvelope
            {
                RelayOpaqueEnvelope = new Percolator.Contracts.RelayOpaqueEnvelope
                {
                    Version = 1,
                    OpaquePayload = Google.Protobuf.ByteString.CopyFrom(item.OpaqueBytes),
                    MessageAckId = Google.Protobuf.ByteString.CopyFrom(item.AckId.ToByteArray())
                }
            };

            var cipher = await _peerRuntime.EncryptInternalEnvelopeAsync(
                relayHostPeerId,
                relayHostToMainSessionId,
                env,
                cancellationToken).ConfigureAwait(false);

            var request = new Percolator.Contracts.DeliverOpaqueMessageRequest
            {
                Version = 1,
                Payload = Google.Protobuf.ByteString.CopyFrom(cipher.Value)
            };

            var ctx = new ServerCallContextStub(
                peer: "ipv4:127.0.0.1:0",
                deadline: DateTime.UtcNow.AddMinutes(1),
                requestHeaders: new Grpc.Core.Metadata(),
                cancellationToken: cancellationToken);

            var response = await _messageService.DeliverOpaqueMessage(request, ctx).ConfigureAwait(false);

            if (response.ResultCase != Percolator.Contracts.DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload
                || response.ResponsePayload is null
                || !response.ResponsePayload.HasResponsePayload)
            {
                break;
            }

            var ackCipher = new Percolator.Cryptography.SessionRatchetMessage(response.ResponsePayload.ResponsePayload.ToByteArray());
            var ackPlain = await _peerRuntime.DecryptSessionMessageAsync(relayHostPeerId, relayHostToMainSessionId, ackCipher, cancellationToken).ConfigureAwait(false);
            var ack = Percolator.Contracts.RelayOpaqueResponse.Parser.ParseFrom(ackPlain.Value);
            if (!ack.HasMessageAckId)
            {
                break;
            }

            var ackId = item.AckId;
            var returnedAck = new Guid(ack.MessageAckId.ToByteArray());
            if (returnedAck != ackId)
            {
                break;
            }

            _ = await _state.DeleteRelayOpaqueByAckIdAsync(relayHostPeerId, ackId, cancellationToken).ConfigureAwait(false);
            forwarded++;
        }

        return forwarded;
    }

    public async Task<int> ForwardQueuedToPeerAsync(
        Guid relayHostPeerId,
        Guid recipientPeerId,
        int max,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (recipientPeerId == MainNodeSentinelPeerId)
        {
            throw new InvalidOperationException("ForwardQueuedToPeerAsync cannot target Main by Guid. Use ForwardQueuedToMainByRoutingKeyAsync (PKH) instead.");
        }

        var recipientRoutingKey = await _peerRuntime.ComputePublicKeyHashAsync(recipientPeerId, cancellationToken).ConfigureAwait(false);
        var dequeued = await _state
            .DequeueRelayOpaqueAsync(relayHostPeerId, recipientRoutingKey, max, cancellationToken)
            .ConfigureAwait(false);
        if (dequeued.Count == 0)
        {
            return 0;
        }

        var forwarded = 0;
        foreach (var item in dequeued)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _ = await _peerRuntime
                .ReceiveRelayedOpaquePayloadAsync(recipientPeerId, item.OpaqueBytes, cancellationToken)
                .ConfigureAwait(false);

            forwarded++;
        }

        return forwarded;
    }

    public async Task<int> ForwardQueuedToMainByRoutingKeyAsync(
        Guid relayHostPeerId,
        byte[] recipientRoutingKey,
        Percolator.Cryptography.SessionId relayHostToMainSessionId,
        CancellationToken cancellationToken = default)
    {
        if (recipientRoutingKey is null) throw new ArgumentNullException(nameof(recipientRoutingKey));
        cancellationToken.ThrowIfCancellationRequested();

        var forwarded = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var d = await _state.PeekRelayOpaqueAsync(relayHostPeerId, recipientRoutingKey, cancellationToken).ConfigureAwait(false);
            if (d is null)
            {
                break;
            }
            var env = new Percolator.Contracts.InternalEnvelope
            {
                RelayOpaqueEnvelope = new Percolator.Contracts.RelayOpaqueEnvelope
                {
                    Version = 1,
                    OpaquePayload = Google.Protobuf.ByteString.CopyFrom(d.OpaqueBytes),
                    MessageAckId = Google.Protobuf.ByteString.CopyFrom(d.AckId.ToByteArray())
                }
            };

            var cipher = await _peerRuntime.EncryptInternalEnvelopeAsync(
                relayHostPeerId,
                relayHostToMainSessionId,
                env,
                cancellationToken).ConfigureAwait(false);

            var request = new Percolator.Contracts.DeliverOpaqueMessageRequest
            {
                Version = 1,
                Payload = Google.Protobuf.ByteString.CopyFrom(cipher.Value)
            };

            var ctx = new ServerCallContextStub(
                peer: "ipv4:127.0.0.1:0",
                deadline: DateTime.UtcNow.AddMinutes(1),
                requestHeaders: new Grpc.Core.Metadata(),
                cancellationToken: cancellationToken);

            var response = await _messageService.DeliverOpaqueMessage(request, ctx).ConfigureAwait(false);

            if (response.ResultCase != Percolator.Contracts.DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload
                || response.ResponsePayload is null
                || !response.ResponsePayload.HasResponsePayload)
            {
                break;
            }

            var ackCipher = new Percolator.Cryptography.SessionRatchetMessage(response.ResponsePayload.ResponsePayload.ToByteArray());
            var ackPlain = await _peerRuntime.DecryptSessionMessageAsync(relayHostPeerId, relayHostToMainSessionId, ackCipher, cancellationToken).ConfigureAwait(false);
            var ack = Percolator.Contracts.RelayOpaqueResponse.Parser.ParseFrom(ackPlain.Value);
            if (!ack.HasMessageAckId)
            {
                break;
            }

            var ackId = d.AckId;
            var returnedAck = new Guid(ack.MessageAckId.ToByteArray());
            if (returnedAck != ackId)
            {
                break;
            }

            _ = await _state.DeleteRelayOpaqueByAckIdAsync(relayHostPeerId, ackId, cancellationToken).ConfigureAwait(false);
            forwarded++;
        }

        return forwarded;
    }

    private sealed class ServerCallContextStub : Grpc.Core.ServerCallContext
    {
        private readonly string _peer;
        private readonly DateTime _deadline;
        private readonly Grpc.Core.Metadata _requestHeaders;
        private readonly CancellationToken _cancellationToken;

        public ServerCallContextStub(string peer, DateTime deadline, Grpc.Core.Metadata requestHeaders, CancellationToken cancellationToken)
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
        protected override Grpc.Core.Metadata RequestHeadersCore => _requestHeaders;
        protected override CancellationToken CancellationTokenCore => _cancellationToken;
        protected override Grpc.Core.Metadata ResponseTrailersCore { get; } = new Grpc.Core.Metadata();
        protected override Grpc.Core.Status StatusCore { get; set; }
        protected override Grpc.Core.WriteOptions? WriteOptionsCore { get; set; }
        protected override Grpc.Core.AuthContext AuthContextCore { get; } = new Grpc.Core.AuthContext(null, new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Grpc.Core.AuthProperty>>());

        protected override Grpc.Core.ContextPropagationToken CreatePropagationTokenCore(Grpc.Core.ContextPropagationOptions? options) => throw new NotImplementedException();
        protected override Task WriteResponseHeadersAsyncCore(Grpc.Core.Metadata responseHeaders) => Task.CompletedTask;
    }
}
