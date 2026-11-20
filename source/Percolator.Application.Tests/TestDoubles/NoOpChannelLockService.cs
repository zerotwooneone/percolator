using Percolator.Application.Ports;
using Percolator.Domain.Channels.ValueObjects;

namespace Percolator.Application.Tests.TestDoubles;

public sealed class NoOpChannelLockService : IChannelLockService
{
    private static readonly IDisposable Releaser = new EmptyDisposable();

    public ValueTask<IDisposable> AcquireLockAsync(ChannelId channelId, CancellationToken ct = default)
    {
        return ValueTask.FromResult(Releaser);
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
