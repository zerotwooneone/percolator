using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.NetworkInformation;
using Percolator.Infrastructure.Network.Certificates;

namespace Percolator.Infrastructure.Network;

public class NetworkEnvironment : Percolator.Application.Network.INetworkEnvironment
{
    private readonly ILogger<NetworkEnvironment> _logger;
    private readonly TlsOptions _tlsOptions;

    public NetworkEnvironment(
        ILogger<NetworkEnvironment> logger,
        IOptions<TlsOptions> tlsOptions)
    {
        _logger = logger;
        _tlsOptions = tlsOptions.Value;
    }

    public Task<int> GetAvailablePortAsync(IEnumerable<int> exclusionList,CancellationToken ct)
    {
        try
        {
            var startRange = _tlsOptions.PortRange.Start;
            var endRange = _tlsOptions.PortRange.End;

            // Get active TCP listeners
            var activeListeners = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(l => l.Port)
                .ToHashSet();

            var excludePorts = exclusionList as int[] ?? exclusionList.ToArray();
            // Find first available port
            for (int port = startRange; port <= endRange; port++)
            {
                if (!excludePorts.Contains(port) && !activeListeners.Contains(port))
                {
                    _logger.LogDebug("Found available port: {Port}", port);
                    return Task.FromResult(port);
                }
            }

            throw new InvalidOperationException($"No available ports in range {startRange}-{endRange}");
        }
        catch (Exception exception)
        {
            return Task.FromException<int>(exception);
        }
    }
}
