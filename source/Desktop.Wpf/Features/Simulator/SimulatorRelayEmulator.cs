using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

    Task<IReadOnlyList<SimulatedRelayItem>> FetchFromRelayHostAsync(Guid relayHostPeerId, Guid recipientPeerId, int max, CancellationToken cancellationToken = default);

    Task<bool> DeleteByAckIdAsync(Guid relayHostPeerId, Guid recipientPeerId, Guid ackId, CancellationToken cancellationToken = default);

    Task<int> ForwardQueuedToMainAsync(
        Guid relayHostPeerId,
        Guid recipientPeerId,
        Percolator.Cryptography.SessionId relayHostToMainSessionId,
        CancellationToken cancellationToken = default);
}

public sealed class SimulatorRelayEmulator : ISimulatorRelayEmulator
{
    private readonly ISimulatorStateService _state;

    private readonly ISimulatedPeerRuntimeService _peerRuntime;
    private readonly Percolator.Application.Network.PercolatorMessageService _messageService;

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

        var routingKey = recipientPeerId.ToByteArray();
        return _state.EnqueueRelayOpaqueAsync(relayHostPeerId, routingKey, opaqueBytes, debugType, cancellationToken);
    }

    public async Task<IReadOnlyList<SimulatedRelayItem>> FetchFromRelayHostAsync(
        Guid relayHostPeerId,
        Guid recipientPeerId,
        int max,
        CancellationToken cancellationToken = default)
    {
        var routingKey = recipientPeerId.ToByteArray();
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

        var forwarded = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = await FetchFromRelayHostAsync(relayHostPeerId, recipientPeerId, max: 1, cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                break;
            }

            var item = batch[0];
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

            await _messageService.DeliverOpaqueMessage(request, ctx).ConfigureAwait(false);
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
