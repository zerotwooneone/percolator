namespace Percolator.Infrastructure.Tests.Fakes;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;

/// <summary>
/// Minimal in-memory <see cref="IRelayMailboxCapabilityStore"/> test double.
/// Implements only the interface contract — no chaos/inspection surface.
/// </summary>
public sealed class FakeRelayMailboxCapabilityStore : IRelayMailboxCapabilityStore
{
    private sealed class MailboxEntry
    {
        public byte[] DeliveryToken { get; set; } = Array.Empty<byte>();
        public List<byte[]> AuthorizedDeliveryTokens { get; } = new();
        public byte[] DrainToken { get; set; } = Array.Empty<byte>();
        public List<QueuedSealedEnvelopeProto> Envelopes { get; } = new();
        public List<ChannelWriter<QueuedSealedEnvelopeProto>> Subscribers { get; } = new();
        public object Lock { get; } = new();
    }

    private readonly ConcurrentDictionary<string, MailboxEntry> _mailboxes = new();

    private static string ToHexKey(ReadOnlyMemory<byte> token) => Convert.ToHexString(token.Span).ToLowerInvariant();

    public ValueTask RegisterAsync(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> deliveryToken,
        ReadOnlyMemory<byte> drainToken,
        CancellationToken ct = default)
    {
        var result = RegisterVerifiedInternal(routingToken, deliveryToken, drainToken, default);
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
        CancellationToken ct = default)
    {
        var result = RegisterVerifiedInternal(routingToken, deliveryToken, drainToken, existingDrainToken);
        return ValueTask.FromResult(result);
    }

    private DomainResult RegisterVerifiedInternal(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> deliveryToken,
        ReadOnlyMemory<byte> drainToken,
        ReadOnlyMemory<byte> existingDrainToken)
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
        }

        return DomainResult.Success();
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
        if (_mailboxes.TryGetValue(key, out var entry))
        {
            lock (entry.Lock)
            {
                if (entry.DrainToken.Length > 0)
                {
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(entry.DrainToken);
                }
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
