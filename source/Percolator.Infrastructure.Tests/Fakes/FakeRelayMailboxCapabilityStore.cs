namespace Percolator.Infrastructure.Tests.Fakes;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;

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
        public List<RelayQueuedEnvelopeRecord> Envelopes { get; } = new();
        public List<ChannelWriter<RelayQueuedEnvelopeRecord>> Subscribers { get; } = new();
        public object Lock { get; } = new();
    }

    private readonly ConcurrentDictionary<BlindedRoutingToken, MailboxEntry> _mailboxes = new();

    public ValueTask RegisterAsync(
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        DeliveryToken drainToken,
        CancellationToken ct = default)
    {
        var result = RegisterVerifiedInternal(routingToken, deliveryToken, drainToken, null);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error.Description);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<DomainResult> RegisterVerifiedAsync(
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        DeliveryToken drainToken,
        DeliveryToken? existingDrainToken = null,
        CancellationToken ct = default)
    {
        var result = RegisterVerifiedInternal(routingToken, deliveryToken, drainToken, existingDrainToken);
        return ValueTask.FromResult(result);
    }

    private DomainResult RegisterVerifiedInternal(
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        DeliveryToken drainToken,
        DeliveryToken? existingDrainToken)
    {
        var entry = _mailboxes.GetOrAdd(routingToken, _ => new MailboxEntry());
        lock (entry.Lock)
        {
            if (entry.DrainToken.Length > 0)
            {
                bool drainTokenMatches = drainToken.Length == entry.DrainToken.Length &&
                    CryptographicOperations.FixedTimeEquals(entry.DrainToken, drainToken.Span);

                bool existingDrainTokenMatches = existingDrainToken != null &&
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

    public ValueTask<bool> VerifyDeliveryTokenAsync(BlindedRoutingToken routingToken, DeliveryToken deliveryToken, CancellationToken ct = default)
    {
        if (!_mailboxes.TryGetValue(routingToken, out var entry))
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

    public ValueTask<bool> VerifyDrainTokenAsync(BlindedRoutingToken routingToken, DeliveryToken drainToken, CancellationToken ct = default)
    {
        if (!_mailboxes.TryGetValue(routingToken, out var entry))
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

    public ValueTask<DeliveryToken?> GetDrainTokenAsync(BlindedRoutingToken routingToken, CancellationToken ct = default)
    {
        if (_mailboxes.TryGetValue(routingToken, out var entry))
        {
            lock (entry.Lock)
            {
                if (entry.DrainToken.Length > 0)
                {
                    return ValueTask.FromResult<DeliveryToken?>(DeliveryToken.FromSpan(entry.DrainToken));
                }
            }
        }

        return ValueTask.FromResult<DeliveryToken?>(null);
    }

    public ValueTask EnqueueEnvelopeAsync(BlindedRoutingToken routingToken, RelayQueuedEnvelopeRecord envelope, CancellationToken ct = default)
    {
        var entry = _mailboxes.GetOrAdd(routingToken, _ => new MailboxEntry());
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

    public ValueTask<DomainResult> EnqueueEnvelopeAsync(
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        MailboxEnvelope envelope,
        long maxMailboxBytes,
        CancellationToken ct = default)
    {
        if (!_mailboxes.TryGetValue(routingToken, out var entry))
        {
            return ValueTask.FromResult(DomainResult.Failure(new DomainError("MAILBOX_NOT_FOUND", "Mailbox not found.")));
        }

        lock (entry.Lock)
        {
            bool authorized = entry.DeliveryToken.Length > 0 &&
                entry.DeliveryToken.Length == deliveryToken.Length &&
                CryptographicOperations.FixedTimeEquals(entry.DeliveryToken, deliveryToken.Span);

            if (!authorized)
            {
                for (int i = 0; i < entry.AuthorizedDeliveryTokens.Count; i++)
                {
                    var tok = entry.AuthorizedDeliveryTokens[i];
                    if (tok.Length == deliveryToken.Length &&
                        CryptographicOperations.FixedTimeEquals(tok, deliveryToken.Span))
                    {
                        authorized = true;
                        break;
                    }
                }
            }

            if (!authorized)
            {
                return ValueTask.FromResult(DomainResult.Failure(new DomainError("PERMISSION_DENIED", "Invalid delivery token.")));
            }

            long currentBytes = 0;
            foreach (var env in entry.Envelopes)
            {
                currentBytes += env.EnvelopePayload.Length;
            }

            if (currentBytes + envelope.Ciphertext.Length > maxMailboxBytes)
            {
                return ValueTask.FromResult(DomainResult.Failure(new DomainError("MAILBOX_QUOTA_EXCEEDED", "Mailbox byte quota exceeded.")));
            }

            var record = new RelayQueuedEnvelopeRecord(
                MailboxQueueId.Parse(envelope.Id.Value.ToString("N")),
                envelope.Ciphertext,
                DateTimeOffset.UtcNow);

            entry.Envelopes.Add(record);
            for (int i = 0; i < entry.Subscribers.Count; i++)
            {
                entry.Subscribers[i].TryWrite(record);
            }

            return ValueTask.FromResult(DomainResult.Success());
        }
    }

    public ValueTask<DomainResult> EnqueueAuthenticatedInitialAsync(
        BlindedRoutingToken routingToken,
        MailboxEnvelope envelope,
        long maxMailboxBytes,
        CancellationToken ct = default)
    {
        if (!_mailboxes.TryGetValue(routingToken, out var entry))
        {
            return ValueTask.FromResult(DomainResult.Failure(new DomainError("MAILBOX_NOT_FOUND", "Mailbox not found.")));
        }

        lock (entry.Lock)
        {
            long currentBytes = 0;
            foreach (var env in entry.Envelopes)
            {
                currentBytes += env.EnvelopePayload.Length;
            }

            if (currentBytes + envelope.Ciphertext.Length > maxMailboxBytes)
            {
                return ValueTask.FromResult(DomainResult.Failure(new DomainError("MAILBOX_QUOTA_EXCEEDED", "Mailbox byte quota exceeded.")));
            }

            var record = new RelayQueuedEnvelopeRecord(
                MailboxQueueId.Parse(envelope.Id.Value.ToString("N")),
                envelope.Ciphertext,
                DateTimeOffset.UtcNow);

            entry.Envelopes.Add(record);
            for (int i = 0; i < entry.Subscribers.Count; i++)
            {
                entry.Subscribers[i].TryWrite(record);
            }

            return ValueTask.FromResult(DomainResult.Success());
        }
    }

    public ValueTask<IReadOnlyList<RelayQueuedEnvelopeRecord>> GetEnvelopesAsync(BlindedRoutingToken routingToken, CancellationToken ct = default)
    {
        if (!_mailboxes.TryGetValue(routingToken, out var entry))
        {
            return ValueTask.FromResult<IReadOnlyList<RelayQueuedEnvelopeRecord>>(Array.Empty<RelayQueuedEnvelopeRecord>());
        }

        lock (entry.Lock)
        {
            return ValueTask.FromResult<IReadOnlyList<RelayQueuedEnvelopeRecord>>(entry.Envelopes.ToArray());
        }
    }

    public ValueTask<int> PurgeMailboxAsync(BlindedRoutingToken routingToken, CancellationToken ct = default)
    {
        if (!_mailboxes.TryGetValue(routingToken, out var entry))
        {
            return ValueTask.FromResult(0);
        }

        lock (entry.Lock)
        {
            var purged = entry.Envelopes.Count;
            entry.Envelopes.Clear();
            return ValueTask.FromResult(purged);
        }
    }

    public IDisposable SubscribeMailbox(BlindedRoutingToken routingToken, ChannelWriter<RelayQueuedEnvelopeRecord> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var entry = _mailboxes.GetOrAdd(routingToken, _ => new MailboxEntry());
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

    public ValueTask<bool> AcknowledgeEnvelopeAsync(BlindedRoutingToken routingToken, MailboxQueueId queueId, CancellationToken ct = default)
    {
        if (_mailboxes.TryGetValue(routingToken, out var entry))
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
