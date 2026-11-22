using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Application.Ports;

namespace Percolator.Infrastructure.Vault;

public sealed class AuthenticatedSession : IAuthenticatedSession
{
    private readonly IHost _host;
    private readonly ILogger<AuthenticatedSession> _logger;
    private bool _disposed;
    private bool _started;

    public AuthenticatedSession(IHost host, ILogger<AuthenticatedSession> logger)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IServiceProvider Services => _host.Services;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _host.StartAsync(ct).ConfigureAwait(false);
        _started = true;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_started) return;
        await _host.StopAsync(ct).ConfigureAwait(false);
        _started = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_started)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _host.StopAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping authenticated session host during teardown");
            }
        }

        _host.Dispose();
    }
}
