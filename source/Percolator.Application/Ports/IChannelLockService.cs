using Percolator.Domain.Channels.ValueObjects;

namespace Percolator.Application2.Ports;

/// <summary>
/// Provides asynchronous keyed locking scoped by channel or session ID,
/// ensuring sequential ratchet advancement without concurrent interleaving.
/// </summary>
public interface IChannelLockService
{
    ValueTask<IDisposable> AcquireLockAsync(ChannelId channelId, CancellationToken ct = default);
}
