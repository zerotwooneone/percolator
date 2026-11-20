using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;

namespace Percolator.Application.Delivery.Ports;

/// <summary>
/// Live session over an authenticated bidirectional mailbox stream. The server
/// re-delivers unacknowledged envelopes, so acknowledgement means durable local
/// receipt — never "processed".
/// </summary>
public interface IMailboxStreamSession : IAsyncDisposable
{
    /// <summary>Ordered envelopes as they arrive; completes when the stream ends.</summary>
    IAsyncEnumerable<QueuedMailboxEnvelope> Envelopes { get; }

    /// <summary>Deletes <paramref name="queueId"/> from the relay queue after durable local persistence.</summary>
    Task<DomainResult> AcknowledgeAsync(MailboxQueueId queueId, CancellationToken ct = default);
}
