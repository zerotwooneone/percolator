using System.Net;
using Grpc.Net.Client;
using Percolator.Contracts;
using Percolator.Network;

namespace Percolator.Application.Network;

public interface IGrpcClientFactory
{
    TransportService.TransportServiceClient CreateClient(DnsEndPoint endpoint, TlsCertificate? tlsCertificate);
}
