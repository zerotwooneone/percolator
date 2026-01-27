using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    void EnqueueToRelayHost(Guid relayHostPeerId, Guid recipientPeerId, byte[] opaqueBytes, string? debugType = null);

    IReadOnlyList<SimulatedRelayItem> FetchFromRelayHost(Guid relayHostPeerId, Guid recipientPeerId, int max);

    bool DeleteByAckId(Guid relayHostPeerId, Guid recipientPeerId, Guid ackId);

    Task<int> ForwardQueuedToMainAsync(
        Guid relayHostPeerId,
        Guid recipientPeerId,
        Percolator.Cryptography.SessionId relayHostToMainSessionId,
        CancellationToken cancellationToken = default);
}

public sealed class SimulatorRelayEmulator : ISimulatorRelayEmulator
{
    private readonly ConcurrentDictionary<(Guid relayHostPeerId, Guid recipientPeerId), ConcurrentQueue<SimulatedRelayItem>> _queues = new();

    private readonly ISimulatedPeerRuntimeService _peerRuntime;
    private readonly Percolator.Application.Network.PercolatorMessageService _messageService;

    public SimulatorRelayEmulator(
        ISimulatedPeerRuntimeService peerRuntime,
        Percolator.Application.Network.PercolatorMessageService messageService)
    {
        _peerRuntime = peerRuntime;
        _messageService = messageService;
    }

    public void EnqueueToRelayHost(Guid relayHostPeerId, Guid recipientPeerId, byte[] opaqueBytes, string? debugType = null)
    {
        if (opaqueBytes is null) throw new ArgumentNullException(nameof(opaqueBytes));

        var queue = _queues.GetOrAdd((relayHostPeerId, recipientPeerId), static _ => new ConcurrentQueue<SimulatedRelayItem>());
        queue.Enqueue(new SimulatedRelayItem(
            RelayHostPeerId: relayHostPeerId,
            RecipientPeerId: recipientPeerId,
            AckId: Guid.NewGuid(),
            OpaqueBytes: opaqueBytes,
            EnqueuedUtc: DateTimeOffset.UtcNow,
            DebugType: debugType));
    }

    public IReadOnlyList<SimulatedRelayItem> FetchFromRelayHost(Guid relayHostPeerId, Guid recipientPeerId, int max)
    {
        if (max <= 0) return Array.Empty<SimulatedRelayItem>();

        if (!_queues.TryGetValue((relayHostPeerId, recipientPeerId), out var queue))
        {
            return Array.Empty<SimulatedRelayItem>();
        }

        var list = new List<SimulatedRelayItem>(Math.Min(max, 64));
        for (var i = 0; i < max; i++)
        {
            if (!queue.TryDequeue(out var item))
            {
                break;
            }

            list.Add(item);
        }

        return list;
    }

    public bool DeleteByAckId(Guid relayHostPeerId, Guid recipientPeerId, Guid ackId)
    {
        return false;
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

            var batch = FetchFromRelayHost(relayHostPeerId, recipientPeerId, max: 1);
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
