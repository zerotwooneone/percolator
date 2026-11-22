namespace Percolator.Application.Tests.TestDoubles;

using System;
using Percolator.PluginSdk;

public sealed class FakeNetworkEndpointDiscoveryPort : INetworkEndpointDiscoveryPort
{
    public Uri? DirectEndpoint { get; set; }

    public Uri? GetLocalDirectEndpoint() => DirectEndpoint;
}
