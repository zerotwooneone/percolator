namespace Percolator.Desktop.Common.Services;

using System;
using Percolator.PluginSdk;

public sealed class DesktopNetworkEndpointDiscoveryPort : INetworkEndpointDiscoveryPort
{
    private readonly ICurrentPersonaService _currentPersonaService;

    public DesktopNetworkEndpointDiscoveryPort(ICurrentPersonaService currentPersonaService)
    {
        _currentPersonaService = currentPersonaService ?? throw new ArgumentNullException(nameof(currentPersonaService));
    }

    public Uri? GetLocalDirectEndpoint()
    {
        var boundPort = _currentPersonaService.CurrentPersona?.BoundPort;
        if (boundPort is > 0)
        {
            return new Uri($"https://127.0.0.1:{boundPort}");
        }

        return null;
    }
}
