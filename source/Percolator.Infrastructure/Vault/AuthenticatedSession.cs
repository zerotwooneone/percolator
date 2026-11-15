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

    public AuthenticatedSession(IHost host, ILogger<AuthenticatedSession> logger)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping authenticated session host during teardown");
        }

        _host.Dispose();
    }
}
