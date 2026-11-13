using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Ports;

/// <summary>
/// Null Object for <see cref="ITransferStreamPool"/> used when a node has no connected
/// stream pool (e.g. handlers resolved before pool establishment). Connection and
/// broadcast calls are no-ops; stream access reports the capability as unavailable.
/// </summary>
public sealed class NullTransferStreamPool : ITransferStreamPool
{
    public static readonly NullTransferStreamPool Instance = new();

    private NullTransferStreamPool()
    {
    }

    public StreamCount ActiveStreams => StreamCount.FromInt(0);

    public ValueTask ConnectDirectAsync(IReadOnlyList<DirectEndpointCandidate> candidates, CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask ConnectRelayPipeAsync(RelayRendezvousDescriptor rendezvous, CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }

    public ITransferStream GetStream(StreamIndex index)
    {
        throw new InvalidOperationException("No transfer stream pool is connected on this node.");
    }

    public ValueTask BroadcastFrameAsync(IStreamFramePayload frame, CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
