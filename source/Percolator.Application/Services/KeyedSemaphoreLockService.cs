using System.Collections.Concurrent;
using Percolator.Application2.Ports;
using Percolator.Domain.Channels.ValueObjects;

namespace Percolator.Application2.Services;

/// <summary>
/// In-process default implementation of IChannelLockService using keyed semaphores.
/// </summary>
public sealed class KeyedSemaphoreLockService : IChannelLockService
{
    private readonly ConcurrentDictionary<ChannelId, SemaphoreSlim> _locks = new();

    public async ValueTask<IDisposable> AcquireLockAsync(ChannelId channelId, CancellationToken ct = default)
    {
        var semaphore = _locks.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(semaphore);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public Releaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            var sem = Interlocked.Exchange(ref _semaphore, null);
            sem?.Release();
        }
    }
}
