namespace Percolator.PluginSdk;

using System;

/// <summary>
/// Port for discovering local reachable direct endpoints (e.g. HTTP/2 or gRPC listening endpoint).
/// </summary>
public interface INetworkEndpointDiscoveryPort
{
    Uri? GetLocalDirectEndpoint();
}
