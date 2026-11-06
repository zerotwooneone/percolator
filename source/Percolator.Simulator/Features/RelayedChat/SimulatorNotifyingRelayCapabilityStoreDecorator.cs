using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.RelayedChat;

internal sealed class SimulatorNotifyingRelayCapabilityStoreDecorator : IRelayMailboxCapabilityStore, IRelayQueueChaosController
{
    private sealed class MailboxEntry
    {
        public byte[] DeliveryToken { get; set; } = Array.Empty<byte>();
        public byte[] DrainToken { get; set; } = Array.Empty<byte>();
        public PublicIdentityId? OwnerId { get; set; }
        public DeviceId? DeviceId { get; set; }
        public List<QueuedSealedEnvelopeProto> Envelopes { get; } = new();
        public object Lock { get; } = new();
    }

    private readonly ISimulatorStateBus _stateBus;
    private readonly SimulatedNodeId _nodeId;
    private readonly ConcurrentDictionary<string, MailboxEntry> _mailboxes = new();

    public SimulatorNotifyingRelayCapabilityStoreDecorator(ISimulatorStateBus stateBus, SimulatedNodeId nodeId)
    {
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _nodeId = nodeId;
    }

    private static string ToHexKey(ReadOnlyMemory<byte> token) => Convert.ToHexString(token.Span).ToLowerInvariant();

    public int TotalEnvelopeCount
    {
        get
        {
            int count = 0;
            foreach (var entry in _mailboxes.Values)
            {
                lock (entry.Lock)
                {
                    count += entry.Envelopes.Count;
                }
            }
            return count;
        }
    }

    public IReadOnlyList<QueuedSealedEnvelopeProto> GetHeldEnvelopes()
    {
        var list = new List<QueuedSealedEnvelopeProto>();
        foreach (var entry in _mailboxes.Values)
        {
            lock (entry.Lock)
            {
                list.AddRange(entry.Envelopes);
            }
        }
        return list;
    }

    public ValueTask RegisterAsync(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> deliveryToken,
        ReadOnlyMemory<byte> drainToken,
        PublicIdentityId? ownerId = null,
        DeviceId? deviceId = null,
        CancellationToken ct = default)
    {
        var key = ToHexKey(routingToken);
        var entry = _mailboxes.GetOrAdd(key, _ => new MailboxEntry());
        lock (entry.Lock)
        {
            entry.DeliveryToken = deliveryToken.ToArray();
            entry.DrainToken = drainToken.ToArray();
            if (ownerId.HasValue) entry.OwnerId = ownerId;
            if (deviceId.HasValue) entry.DeviceId = deviceId;
        }

        _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "MailboxRegistered"));
        return ValueTask.CompletedTask;
    }

    public ValueTask AssociateOwnerAsync(
        ReadOnlyMemory<byte> routingToken,
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken ct = default)
    {
        var key = ToHexKey(routingToken);
        var entry = _mailboxes.GetOrAdd(key, _ => new MailboxEntry());
        lock (entry.Lock)
        {
            entry.OwnerId = ownerId;
            entry.DeviceId = deviceId;
        }

        _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "OwnerAssociated"));
        return ValueTask.CompletedTask;
    }

    public ValueTask<(PublicIdentityId? OwnerId, DeviceId? DeviceId)> GetOwnerAsync(
        ReadOnlyMemory<byte> routingToken,
        CancellationToken ct = default)
    {
        var key = ToHexKey(routingToken);
        if (_mailboxes.TryGetValue(key, out var entry))
        {
            lock (entry.Lock)
            {
                return ValueTask.FromResult((entry.OwnerId, entry.DeviceId));
            }
        }
        return ValueTask.FromResult<(PublicIdentityId?, DeviceId?)>((null, null));
    }

    public ValueTask<bool> VerifyDeliveryTokenAsync(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> deliveryToken,
        CancellationToken ct = default)
    {
        var key = ToHexKey(routingToken);
        if (!_mailboxes.TryGetValue(key, out var entry))
        {
            return ValueTask.FromResult(false);
        }

        lock (entry.Lock)
        {
            if (entry.DeliveryToken.Length == 0 || entry.DeliveryToken.Length != deliveryToken.Length)
            {
                return ValueTask.FromResult(false);
            }

            bool match = CryptographicOperations.FixedTimeEquals(entry.DeliveryToken, deliveryToken.Span);
            return ValueTask.FromResult(match);
        }
    }

    public ValueTask<bool> VerifyDrainTokenAsync(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> drainToken,
        CancellationToken ct = default)
    {
        var key = ToHexKey(routingToken);
        if (!_mailboxes.TryGetValue(key, out var entry))
        {
            return ValueTask.FromResult(false);
        }

        lock (entry.Lock)
        {
            if (entry.DrainToken.Length == 0 || entry.DrainToken.Length != drainToken.Length)
            {
                return ValueTask.FromResult(false);
            }

            bool match = CryptographicOperations.FixedTimeEquals(entry.DrainToken, drainToken.Span);
            return ValueTask.FromResult(match);
        }
    }

    public ValueTask<ReadOnlyMemory<byte>?> GetDrainTokenAsync(
        ReadOnlyMemory<byte> routingToken,
        CancellationToken ct = default)
    {
        var key = ToHexKey(routingToken);
        if (_mailboxes.TryGetValue(key, out var entry))
        {
            lock (entry.Lock)
            {
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(entry.DrainToken.ToArray());
            }
        }
        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
    }

    public ValueTask EnqueueEnvelopeAsync(
        ReadOnlyMemory<byte> routingToken,
        QueuedSealedEnvelopeProto envelope,
        CancellationToken ct = default)
    {
        var key = ToHexKey(routingToken);
        var entry = _mailboxes.GetOrAdd(key, _ => new MailboxEntry());
        lock (entry.Lock)
        {
            entry.Envelopes.Add(envelope);
        }

        _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "EnvelopeEnqueued"));
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<QueuedSealedEnvelopeProto>> DrainEnvelopesAsync(
        ReadOnlyMemory<byte> routingToken,
        CancellationToken ct = default)
    {
        var key = ToHexKey(routingToken);
        if (!_mailboxes.TryGetValue(key, out var entry))
        {
            return ValueTask.FromResult<IReadOnlyList<QueuedSealedEnvelopeProto>>(Array.Empty<QueuedSealedEnvelopeProto>());
        }

        IReadOnlyList<QueuedSealedEnvelopeProto> drained;
        lock (entry.Lock)
        {
            drained = entry.Envelopes.ToArray();
            entry.Envelopes.Clear();
        }

        if (drained.Count > 0)
        {
            _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "EnvelopesDrained"));
        }

        return ValueTask.FromResult(drained);
    }

    // -------------------------------------------------------------
    // Chaos Mutations (CTL-REL-01, CTL-REL-02, CTL-REL-03)
    // -------------------------------------------------------------

    public bool DuplicateEnvelope(string? queueId = null)
    {
        foreach (var entry in _mailboxes.Values)
        {
            lock (entry.Lock)
            {
                var target = queueId == null
                    ? entry.Envelopes.FirstOrDefault()
                    : entry.Envelopes.FirstOrDefault(e => e.QueueId == queueId);

                if (target != null)
                {
                    var clone = new QueuedSealedEnvelopeProto
                    {
                        QueueId = target.QueueId, // same queue_id to verify replay detection
                        EnvelopePayload = target.EnvelopePayload,
                        EnqueuedAtUtc = target.EnqueuedAtUtc
                    };
                    entry.Envelopes.Add(clone);

                    _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "ChaosDuplicate"));
                    return true;
                }
            }
        }
        return false;
    }

    public bool FlipBit(string? queueId = null, int byteOffset = 0)
    {
        foreach (var entry in _mailboxes.Values)
        {
            lock (entry.Lock)
            {
                var targetIndex = queueId == null
                    ? (entry.Envelopes.Count > 0 ? 0 : -1)
                    : entry.Envelopes.FindIndex(e => e.QueueId == queueId);

                if (targetIndex >= 0)
                {
                    var target = entry.Envelopes[targetIndex];
                    var payloadBytes = target.EnvelopePayload.ToByteArray();
                    if (payloadBytes.Length > 0)
                    {
                        try
                        {
                            var proto = SealedEnvelopeProto.Parser.ParseFrom(payloadBytes);
                            if (!proto.EncryptedPackage.IsEmpty)
                            {
                                var pkg = proto.EncryptedPackage.ToByteArray();
                                var offset = Math.Clamp(byteOffset, 0, pkg.Length - 1);
                                pkg[offset] ^= 0x01;
                                proto.EncryptedPackage = ByteString.CopyFrom(pkg);
                                payloadBytes = proto.ToByteArray();
                            }
                            else
                            {
                                payloadBytes[0] ^= 0x01;
                            }
                        }
                        catch
                        {
                            var offset = Math.Clamp(byteOffset, 0, payloadBytes.Length - 1);
                            payloadBytes[offset] ^= 0x01;
                        }

                        var corrupted = new QueuedSealedEnvelopeProto
                        {
                            QueueId = target.QueueId,
                            EnvelopePayload = ByteString.CopyFrom(payloadBytes),
                            EnqueuedAtUtc = target.EnqueuedAtUtc
                        };

                        entry.Envelopes[targetIndex] = corrupted;
                        _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "ChaosFlipBit"));
                        return true;
                    }
                }
            }
        }
        return false;
    }

    public bool ShuffleQueue()
    {
        bool shuffledAny = false;
        foreach (var entry in _mailboxes.Values)
        {
            lock (entry.Lock)
            {
                if (entry.Envelopes.Count > 1)
                {
                    // Fisher-Yates shuffle
                    for (int i = entry.Envelopes.Count - 1; i > 0; i--)
                    {
                        int j = Random.Shared.Next(i + 1);
                        (entry.Envelopes[i], entry.Envelopes[j]) = (entry.Envelopes[j], entry.Envelopes[i]);
                    }
                    shuffledAny = true;
                }
            }
        }

        if (shuffledAny)
        {
            _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "ChaosShuffle"));
        }
        return shuffledAny;
    }
}
