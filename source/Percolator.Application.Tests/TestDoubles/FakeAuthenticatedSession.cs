using Percolator.Application2.Ports;

namespace Percolator.Application.Tests.TestDoubles;

/// <summary>
/// A test double implementing <see cref="IAuthenticatedSession"/> for unit testing application and presentation workflows.
/// </summary>
public sealed class FakeAuthenticatedSession : IAuthenticatedSession
{
    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    public IServiceProvider Services { get; }
    public bool IsStarted { get; private set; }
    public bool IsStopped { get; private set; }
    public bool IsDisposed { get; private set; }

    public FakeAuthenticatedSession(IServiceProvider? services = null)
    {
        Services = services ?? new NullServiceProvider();
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IsStarted = true;
        IsStopped = false;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IsStopped = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        if (Services is IAsyncDisposable asyncDisposable)
        {
            return asyncDisposable.DisposeAsync();
        }

        if (Services is IDisposable disposable)
        {
            disposable.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
