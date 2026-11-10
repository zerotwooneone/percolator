namespace Percolator.Infrastructure.Transport;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Protos;

public interface IRelayMailboxCapabilityStore
{
    ValueTask RegisterAsync(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> deliveryToken,
        ReadOnlyMemory<byte> drainToken,
        PublicIdentityId? ownerId = null,
        DeviceId? deviceId = null,
        CancellationToken ct = default);

    ValueTask<DomainResult> RegisterVerifiedAsync(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> deliveryToken,
        ReadOnlyMemory<byte> drainToken,
        ReadOnlyMemory<byte> existingDrainToken = default,
        PublicIdentityId? ownerId = null,
        DeviceId? deviceId = null,
        CancellationToken ct = default);

    ValueTask AssociateOwnerAsync(
        ReadOnlyMemory<byte> routingToken,
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken ct = default);

    ValueTask<(PublicIdentityId? OwnerId, DeviceId? DeviceId)> GetOwnerAsync(
        ReadOnlyMemory<byte> routingToken,
        CancellationToken ct = default);

    ValueTask<(byte[] RoutingToken, byte[] DeliveryToken)?> GetMailboxTokensByOwnerAsync(
        PublicIdentityId ownerId,
        CancellationToken ct = default);

    ValueTask<bool> VerifyDeliveryTokenAsync(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> deliveryToken,
        CancellationToken ct = default);

    ValueTask<bool> VerifyDrainTokenAsync(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> drainToken,
        CancellationToken ct = default);

    ValueTask<ReadOnlyMemory<byte>?> GetDrainTokenAsync(
        ReadOnlyMemory<byte> routingToken,
        CancellationToken ct = default);

    ValueTask EnqueueEnvelopeAsync(
        ReadOnlyMemory<byte> routingToken,
        QueuedSealedEnvelopeProto envelope,
        CancellationToken ct = default);

    ValueTask<IReadOnlyList<QueuedSealedEnvelopeProto>> GetEnvelopesAsync(
        ReadOnlyMemory<byte> routingToken,
        CancellationToken ct = default);

    ValueTask<IReadOnlyList<QueuedSealedEnvelopeProto>> DrainEnvelopesAsync(
        ReadOnlyMemory<byte> routingToken,
        CancellationToken ct = default);

    IDisposable SubscribeMailbox(
        ReadOnlyMemory<byte> routingToken,
        ChannelWriter<QueuedSealedEnvelopeProto> writer);

    ValueTask<bool> AcknowledgeEnvelopeAsync(
        ReadOnlyMemory<byte> routingToken,
        string queueId,
        CancellationToken ct = default);
}

public sealed class InMemoryRelayMailboxCapabilityStore : IRelayMailboxCapabilityStore
{
    private sealed class MailboxEntry
    {
        public byte[] DeliveryToken { get; set; } = Array.Empty<byte>();
        public List<byte[]> AuthorizedDeliveryTokens { get; } = new();
        public byte[] DrainToken { get; set; } = Array.Empty<byte>();
        public PublicIdentityId? OwnerId { get; set; }
        public DeviceId? DeviceId { get; set; }
        public List<QueuedSealedEnvelopeProto> Envelopes { get; } = new();
        public List<ChannelWriter<QueuedSealedEnvelopeProto>> Subscribers { get; } = new();
        public object Lock { get; } = new();
    }

    private readonly ConcurrentDictionary<string, MailboxEntry> _mailboxes = new();

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
        var result = RegisterVerifiedInternal(routingToken, deliveryToken, drainToken, default, ownerId, deviceId);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error.Description);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<DomainResult> RegisterVerifiedAsync(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> deliveryToken,
        ReadOnlyMemory<byte> drainToken,
        ReadOnlyMemory<byte> existingDrainToken = default,
        PublicIdentityId? ownerId = null,
        DeviceId? deviceId = null,
        CancellationToken ct = default)
    {
        var result = RegisterVerifiedInternal(routingToken, deliveryToken, drainToken, existingDrainToken, ownerId, deviceId);
        return ValueTask.FromResult(result);
    }

    private DomainResult RegisterVerifiedInternal(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> deliveryToken,
        ReadOnlyMemory<byte> drainToken,
        ReadOnlyMemory<byte> existingDrainToken,
        PublicIdentityId? ownerId,
        DeviceId? deviceId)
    {
        var key = ToHexKey(routingToken);
        var entry = _mailboxes.GetOrAdd(key, _ => new MailboxEntry());
        lock (entry.Lock)
        {
            if (entry.DrainToken.Length > 0)
            {
                bool drainTokenMatches = drainToken.Length == entry.DrainToken.Length &&
                    CryptographicOperations.FixedTimeEquals(entry.DrainToken, drainToken.Span);

                bool existingDrainTokenMatches = !existingDrainToken.IsEmpty &&
                    existingDrainToken.Length == entry.DrainToken.Length &&
                    CryptographicOperations.FixedTimeEquals(entry.DrainToken, existingDrainToken.Span);

                if (!drainTokenMatches && !existingDrainTokenMatches)
                {
                    return DomainResult.Failure(new DomainError(
                        "MAILBOX_OWNERSHIP_MISMATCH", "Cannot overwrite existing mailbox without valid drain token."));
                }
            }

            entry.DeliveryToken = deliveryToken.ToArray();
            entry.AuthorizedDeliveryTokens.Add(deliveryToken.ToArray());
            if (entry.AuthorizedDeliveryTokens.Count > 10)
            {
                entry.AuthorizedDeliveryTokens.RemoveAt(0);
            }
            entry.DrainToken = drainToken.ToArray();
            if (ownerId.HasValue) entry.OwnerId = ownerId;
            if (deviceId.HasValue) entry.DeviceId = deviceId;
        }

        return DomainResult.Success();
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

    public ValueTask<(byte[] RoutingToken, byte[] DeliveryToken)?> GetMailboxTokensByOwnerAsync(
        PublicIdentityId ownerId,
        CancellationToken ct = default)
    {
        foreach (var kvp in _mailboxes)
        {
            var entry = kvp.Value;
            lock (entry.Lock)
            {
                if (entry.OwnerId == ownerId)
                {
                    return ValueTask.FromResult<(byte[] RoutingToken, byte[] DeliveryToken)?>((Convert.FromHexString(kvp.Key), entry.DeliveryToken));
                }
            }
        }
        return ValueTask.FromResult<(byte[] RoutingToken, byte[] DeliveryToken)?>(null);
    }

    public ValueTask<bool> VerifyDeliveryTokenAsync(ReadOnlyMemory<byte> routingToken, ReadOnlyMemory<byte> deliveryToken, CancellationToken ct = default)
    {
        var key = ToHexKey(routingToken);
        if (!_mailboxes.TryGetValue(key, out var entry))
        {
            return ValueTask.FromResult(false);
        }

        lock (entry.Lock)
        {
            if (entry.DeliveryToken.Length > 0 && entry.DeliveryToken.Length == deliveryToken.Length &&
                CryptographicOperations.FixedTimeEquals(entry.DeliveryToken, deliveryToken.Span))
            {
                return ValueTask.FromResult(true);
            }

            for (int i = 0; i < entry.AuthorizedDeliveryTokens.Count; i++)
            {
                var tok = entry.AuthorizedDeliveryTokens[i];
                if (tok.Length == deliveryToken.Length &&
                    CryptographicOperations.FixedTimeEquals(tok, deliveryToken.Span))
                {
                    return ValueTask.FromResult(true);
                }
            }
        }

        return ValueTask.FromResult(false);
    }

    public ValueTask<bool> VerifyDrainTokenAsync(ReadOnlyMemory<byte> routingToken, ReadOnlyMemory<byte> drainToken, CancellationToken ct = default)
    {
        var key = ToHexKey(routingToken);
        if (!_mailboxes.TryGetValue(key, out var entry))
        {
            return ValueTask.FromResult(false);
        }

        lock (entry.Lock)
        {
            if (entry.DrainToken.Length > 0 && entry.DrainToken.Length == drainToken.Length &&
                CryptographicOperations.FixedTimeEquals(entry.DrainToken, drainToken.Span))
            {
                return ValueTask.FromResult(true);
            }
        }

        return ValueTask.FromResult(false);
    }

    public ValueTask<ReadOnlyMemory<byte>?> GetDrainTokenAsync(ReadOnlyMemory<byte> routingToken, CancellationToken ct = default)
    {
        var key = ToHexKey(routingToken);
        if (!_mailboxes.TryGetValue(key, out var entry))
        {
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
        }

        lock (entry.Lock)
        {
            if (entry.DrainToken.Length > 0)
            {
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(entry.DrainToken);
            }
        }

        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
    }

    public ValueTask EnqueueEnvelopeAsync(ReadOnlyMemory<byte> routingToken, QueuedSealedEnvelopeProto envelope, CancellationToken ct = default)
    {
        var key = ToHexKey(routingToken);
        var entry = _mailboxes.GetOrAdd(key, _ => new MailboxEntry());
        lock (entry.Lock)
        {
            entry.Envelopes.Add(envelope);
            for (int i = 0; i < entry.Subscribers.Count; i++)
            {
                entry.Subscribers[i].TryWrite(envelope);
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<QueuedSealedEnvelopeProto>> GetEnvelopesAsync(ReadOnlyMemory<byte> routingToken, CancellationToken ct = default)
    {
        var key = ToHexKey(routingToken);
        if (!_mailboxes.TryGetValue(key, out var entry))
        {
            return ValueTask.FromResult<IReadOnlyList<QueuedSealedEnvelopeProto>>(Array.Empty<QueuedSealedEnvelopeProto>());
        }

        lock (entry.Lock)
        {
            return ValueTask.FromResult<IReadOnlyList<QueuedSealedEnvelopeProto>>(entry.Envelopes.ToArray());
        }
    }

    public ValueTask<IReadOnlyList<QueuedSealedEnvelopeProto>> DrainEnvelopesAsync(ReadOnlyMemory<byte> routingToken, CancellationToken ct = default)
    {
        var key = ToHexKey(routingToken);
        if (!_mailboxes.TryGetValue(key, out var entry))
        {
            return ValueTask.FromResult<IReadOnlyList<QueuedSealedEnvelopeProto>>(Array.Empty<QueuedSealedEnvelopeProto>());
        }

        lock (entry.Lock)
        {
            var drained = entry.Envelopes.ToArray();
            entry.Envelopes.Clear();
            return ValueTask.FromResult<IReadOnlyList<QueuedSealedEnvelopeProto>>(drained);
        }
    }

    public IDisposable SubscribeMailbox(ReadOnlyMemory<byte> routingToken, ChannelWriter<QueuedSealedEnvelopeProto> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var key = ToHexKey(routingToken);
        var entry = _mailboxes.GetOrAdd(key, _ => new MailboxEntry());
        lock (entry.Lock)
        {
            entry.Subscribers.Add(writer);
        }

        return new Subscription(() =>
        {
            lock (entry.Lock)
            {
                entry.Subscribers.Remove(writer);
            }
        });
    }

    public ValueTask<bool> AcknowledgeEnvelopeAsync(ReadOnlyMemory<byte> routingToken, string queueId, CancellationToken ct = default)
    {
        var key = ToHexKey(routingToken);
        if (_mailboxes.TryGetValue(key, out var entry))
        {
            lock (entry.Lock)
            {
                int removed = entry.Envelopes.RemoveAll(e => e.QueueId == queueId);
                return ValueTask.FromResult(removed > 0);
            }
        }
        return ValueTask.FromResult(false);
    }

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
                        QueueId = target.QueueId,
                        EnvelopePayload = target.EnvelopePayload,
                        EnqueuedAtUtc = target.EnqueuedAtUtc
                    };
                    entry.Envelopes.Add(clone);
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
                        catch (InvalidProtocolBufferException)
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
                    for (int i = entry.Envelopes.Count - 1; i > 0; i--)
                    {
                        int j = Random.Shared.Next(i + 1);
                        (entry.Envelopes[i], entry.Envelopes[j]) = (entry.Envelopes[j], entry.Envelopes[i]);
                    }
                    shuffledAny = true;
                }
            }
        }
        return shuffledAny;
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _unsubscribe;
        private int _disposed;

        public Subscription(Action unsubscribe) => _unsubscribe = unsubscribe;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _unsubscribe();
            }
        }
    }
}
