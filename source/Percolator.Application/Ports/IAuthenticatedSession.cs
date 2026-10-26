namespace Percolator.Application2.Ports;

/// <summary>
/// Represents an active, authenticated post-unlock session holding the Stage 2 container
/// with all domain repositories, DbContexts, and background workers.
/// </summary>
public interface IAuthenticatedSession : IAsyncDisposable
{
    /// <summary>
    /// Gets the service provider for the active authenticated Stage 2 container.
    /// </summary>
    IServiceProvider Services { get; }

    /// <summary>
    /// Launches all registered <see cref="Microsoft.Extensions.Hosting.IHostedService"/> background worker instances in the Stage 2 container.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Gracefully stops all registered <see cref="Microsoft.Extensions.Hosting.IHostedService"/> instances prior to container teardown.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task StopAsync(CancellationToken ct = default);
}
