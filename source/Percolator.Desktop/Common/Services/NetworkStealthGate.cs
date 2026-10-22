using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Percolator.Domain.Identities.ValueObjects;
using R3;

namespace Percolator.Desktop.Common.Services;

/// <summary>
/// Thread-safe implementation of INetworkStealthGate.
/// Enforces total network silence on startup until explicit user confirmation of identities.
/// </summary>
public sealed class NetworkStealthGate : INetworkStealthGate
{
    private readonly ILogger<NetworkStealthGate> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly BehaviorSubject<bool> _networkActiveSubject = new(false);
    private List<PublicIdentityId> _activeIdentities = [];
    private bool _disposed;

    public NetworkStealthGate(ILogger<NetworkStealthGate> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsStealthActive => !_networkActiveSubject.Value;

    public IReadOnlyList<PublicIdentityId> ActiveIdentities
    {
        get
        {
            lock (_activeIdentities)
            {
                return _activeIdentities.ToArray();
            }
        }
    }

    public Observable<bool> NetworkActiveChanged => _networkActiveSubject;

    public async Task ActivateAsync(IReadOnlyList<PublicIdentityId> identities, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identities);
        if (identities.Count == 0)
        {
            throw new ArgumentException("At least one active identity is required to exit stealth mode.", nameof(identities));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_networkActiveSubject.Value)
            {
                _logger.LogInformation("Network stealth gate already activated. Updating active identities to count: {Count}", identities.Count);
            }
            else
            {
                _logger.LogInformation("Exiting network stealth mode. Activating listeners for {Count} identity/identities.", identities.Count);
            }

            lock (_activeIdentities)
            {
                _activeIdentities = identities.Distinct().ToList();
            }

            _networkActiveSubject.OnNext(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeactivateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_networkActiveSubject.Value)
            {
                _logger.LogDebug("Network stealth gate already in stealth mode.");
                return;
            }

            _logger.LogInformation("Re-entering total network stealth mode. Tearing down all active listeners.");

            lock (_activeIdentities)
            {
                _activeIdentities.Clear();
            }

            _networkActiveSubject.OnNext(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _networkActiveSubject.Dispose();
        _gate.Dispose();
    }
}
