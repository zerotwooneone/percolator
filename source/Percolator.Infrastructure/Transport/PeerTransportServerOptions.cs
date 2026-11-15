namespace Percolator.Infrastructure.Transport;

using System.Net;

public sealed class PeerTransportServerOptions
{
    public IPAddress BindAddress { get; set; } = IPAddress.Any;
}
