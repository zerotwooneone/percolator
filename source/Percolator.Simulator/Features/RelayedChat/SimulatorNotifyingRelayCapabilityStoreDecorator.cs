using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.RelayedChat;

internal sealed class SimulatorNotifyingRelayCapabilityStoreDecorator : IRelayMailboxCapabilityStore, IRelayQueueChaosController
{
    private readonly ISimulatorStateBus _stateBus;
    private readonly SimulatedNodeId _nodeId;
    private readonly IRelayMailboxCapabilityStore _inner;
    private readonly IDbContextFactory<PercolatorDbContext> _contextFactory;

    public SimulatorNotifyingRelayCapabilityStoreDecorator(
        ISimulatorStateBus stateBus,
        SimulatedNodeId nodeId,
        IRelayMailboxCapabilityStore inner,
        IDbContextFactory<PercolatorDbContext> contextFactory)
    {
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _nodeId = nodeId;
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public int TotalEnvelopeCount
    {
        get
        {
            using var db = _contextFactory.CreateDbContext();
            return db.RelayQueuedEnvelopes.Count();
        }
    }

    public IReadOnlyList<QueuedSealedEnvelopeProto> GetHeldEnvelopes()
    {
        using var db = _contextFactory.CreateDbContext();
        return db.RelayQueuedEnvelopes
            .AsNoTracking()
            .OrderBy(e => e.Ordinal)
            .ToList()
            .Select(ToProto)
            .ToList();
    }

    public async ValueTask RegisterAsync(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> deliveryToken,
        ReadOnlyMemory<byte> drainToken,
        PublicIdentityId? ownerId = null,
        DeviceId? deviceId = null,
        CancellationToken ct = default)
    {
        await _inner.RegisterAsync(routingToken, deliveryToken, drainToken, ownerId, deviceId, ct).ConfigureAwait(false);
        _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "MailboxRegistered"));
    }

    public async ValueTask<DomainResult> RegisterVerifiedAsync(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> deliveryToken,
        ReadOnlyMemory<byte> drainToken,
        ReadOnlyMemory<byte> existingDrainToken = default,
        PublicIdentityId? ownerId = null,
        DeviceId? deviceId = null,
        CancellationToken ct = default)
    {
        var result = await _inner.RegisterVerifiedAsync(
            routingToken, deliveryToken, drainToken, existingDrainToken, ownerId, deviceId, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "MailboxRegistered"));
        }

        return result;
    }

    public async ValueTask AssociateOwnerAsync(
        ReadOnlyMemory<byte> routingToken,
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken ct = default)
    {
        await _inner.AssociateOwnerAsync(routingToken, ownerId, deviceId, ct).ConfigureAwait(false);
        _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "OwnerAssociated"));
    }

    public ValueTask<(PublicIdentityId? OwnerId, DeviceId? DeviceId)> GetOwnerAsync(
        ReadOnlyMemory<byte> routingToken,
        CancellationToken ct = default)
    {
        return _inner.GetOwnerAsync(routingToken, ct);
    }

    public ValueTask<(byte[] RoutingToken, byte[] DeliveryToken)?> GetMailboxTokensByOwnerAsync(
        PublicIdentityId ownerId,
        CancellationToken ct = default)
    {
        return _inner.GetMailboxTokensByOwnerAsync(ownerId, ct);
    }

    public ValueTask<bool> VerifyDeliveryTokenAsync(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> deliveryToken,
        CancellationToken ct = default)
    {
        return _inner.VerifyDeliveryTokenAsync(routingToken, deliveryToken, ct);
    }

    public ValueTask<bool> VerifyDrainTokenAsync(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> drainToken,
        CancellationToken ct = default)
    {
        return _inner.VerifyDrainTokenAsync(routingToken, drainToken, ct);
    }

    public ValueTask<ReadOnlyMemory<byte>?> GetDrainTokenAsync(
        ReadOnlyMemory<byte> routingToken,
        CancellationToken ct = default)
    {
        return _inner.GetDrainTokenAsync(routingToken, ct);
    }

    public async ValueTask EnqueueEnvelopeAsync(
        ReadOnlyMemory<byte> routingToken,
        QueuedSealedEnvelopeProto envelope,
        CancellationToken ct = default)
    {
        await _inner.EnqueueEnvelopeAsync(routingToken, envelope, ct).ConfigureAwait(false);
        _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "EnvelopeEnqueued"));
    }

    public ValueTask<IReadOnlyList<QueuedSealedEnvelopeProto>> GetEnvelopesAsync(
        ReadOnlyMemory<byte> routingToken,
        CancellationToken ct = default)
    {
        return _inner.GetEnvelopesAsync(routingToken, ct);
    }

    public async ValueTask<IReadOnlyList<QueuedSealedEnvelopeProto>> DrainEnvelopesAsync(
        ReadOnlyMemory<byte> routingToken,
        CancellationToken ct = default)
    {
        var drained = await _inner.DrainEnvelopesAsync(routingToken, ct).ConfigureAwait(false);
        if (drained.Count > 0)
        {
            _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "EnvelopesDrained"));
        }
        return drained;
    }

    public IDisposable SubscribeMailbox(
        ReadOnlyMemory<byte> routingToken,
        ChannelWriter<QueuedSealedEnvelopeProto> writer)
    {
        return _inner.SubscribeMailbox(routingToken, writer);
    }

    public async ValueTask<bool> AcknowledgeEnvelopeAsync(
        ReadOnlyMemory<byte> routingToken,
        string queueId,
        CancellationToken ct = default)
    {
        bool acknowledged = await _inner.AcknowledgeEnvelopeAsync(routingToken, queueId, ct).ConfigureAwait(false);
        if (acknowledged)
        {
            _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "EnvelopeAcknowledged"));
        }
        return acknowledged;
    }

    public bool DuplicateEnvelope(string? queueId = null)
    {
        using var db = _contextFactory.CreateDbContext();
        var query = db.RelayQueuedEnvelopes.OrderBy(e => e.Ordinal).AsQueryable();
        var target = queueId == null
            ? query.FirstOrDefault()
            : query.FirstOrDefault(e => e.QueueId == queueId);

        if (target == null)
        {
            return false;
        }

        var maxOrdinal = db.RelayQueuedEnvelopes
            .Where(e => e.RoutingToken == target.RoutingToken)
            .Select(e => (long?)e.Ordinal)
            .Max() ?? -1;

        db.RelayQueuedEnvelopes.Add(new RelayQueuedEnvelopeEntity
        {
            QueueId = $"{target.QueueId}-dup",
            RoutingToken = target.RoutingToken,
            Ordinal = maxOrdinal + 1,
            EnvelopePayload = target.EnvelopePayload,
            EnqueuedAtUtc = target.EnqueuedAtUtc
        });
        db.SaveChanges();

        _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "ChaosDuplicate"));
        return true;
    }

    public bool FlipBit(string? queueId = null, int byteOffset = 0)
    {
        using var db = _contextFactory.CreateDbContext();
        var query = db.RelayQueuedEnvelopes.OrderBy(e => e.Ordinal).AsQueryable();
        var target = queueId == null
            ? query.FirstOrDefault()
            : query.FirstOrDefault(e => e.QueueId == queueId);

        if (target == null || target.EnvelopePayload.Length == 0)
        {
            return false;
        }

        var payloadBytes = target.EnvelopePayload.ToArray();
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
        catch (InvalidProtocolBufferException)
        {
            var offset = Math.Clamp(byteOffset, 0, payloadBytes.Length - 1);
            payloadBytes[offset] ^= 0x01;
        }

        target.EnvelopePayload = payloadBytes;
        db.SaveChanges();

        _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "ChaosFlipBit"));
        return true;
    }

    public bool ShuffleQueue()
    {
        using var db = _contextFactory.CreateDbContext();
        var routingTokens = db.RelayQueuedEnvelopes
            .Select(e => e.RoutingToken)
            .Distinct()
            .ToList();

        bool shuffledAny = false;
        foreach (var routing in routingTokens)
        {
            var rows = db.RelayQueuedEnvelopes
                .Where(e => e.RoutingToken == routing)
                .OrderBy(e => e.Ordinal)
                .ToList();

            if (rows.Count <= 1)
            {
                continue;
            }

            var originalOrdinals = rows.Select(r => r.Ordinal).ToArray();
            var permuted = originalOrdinals.ToArray();
            for (int i = permuted.Length - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (permuted[i], permuted[j]) = (permuted[j], permuted[i]);
            }

            // Two-phase update: stage unique sentinel ordinals before assigning the
            // permutation so intermediate values cannot collide on the unique index.
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].Ordinal = -(i + 1);
            }
            db.SaveChanges();

            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].Ordinal = permuted[i];
            }
            db.SaveChanges();
            shuffledAny = true;
        }

        if (shuffledAny)
        {
            _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "ChaosShuffle"));
        }
        return shuffledAny;
    }

    private static QueuedSealedEnvelopeProto ToProto(RelayQueuedEnvelopeEntity entity) => new()
    {
        QueueId = entity.QueueId,
        EnvelopePayload = ByteString.CopyFrom(entity.EnvelopePayload),
        EnqueuedAtUtc = Timestamp.FromDateTimeOffset(entity.EnqueuedAtUtc)
    };
}
