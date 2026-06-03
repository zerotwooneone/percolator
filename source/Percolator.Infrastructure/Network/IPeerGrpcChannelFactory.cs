using Grpc.Net.Client;
using System.Net;

namespace Percolator.Infrastructure.Network;

public interface IPeerGrpcChannelFactory
{
    GrpcChannel CreateChannel(DnsEndPoint endpoint);
}
