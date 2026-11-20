using System.Collections.Generic;
using Percolator.Infrastructure.Protos;

namespace Percolator.Simulator.Features.RelayedChat;

internal interface IRelayQueueChaosController
{
    int TotalEnvelopeCount { get; }
    IReadOnlyList<QueuedSealedEnvelopeProto> GetHeldEnvelopes();
    bool DuplicateEnvelope(string? queueId = null);
    bool FlipBit(string? queueId = null, int byteOffset = 0);
    bool ShuffleQueue();
}
