namespace Percolator.Infrastructure.Vault;

using Microsoft.Extensions.Hosting;
using Percolator.Application2.Ports;

public sealed class AuthenticatedSession : IAuthenticatedSession
{
    private readonly IHost _host;
    private bool _disposed;

    public AuthenticatedSession(IHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public IServiceProvider Services => _host.Services;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _host.StartAsync(ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _host.StopAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            await _host.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Suppress stop errors during teardown
        }

        _host.Dispose();
    }
}
