using System.Net;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using Percolator.Contracts;
using Percolator.Network;

namespace Percolator.Application.Network;

public interface IGrpcClientFactory
{
    TransportService.TransportServiceClient CreateClient(
        DnsEndPoint address, 
        string peerName,
        X509Certificate2 clientCertificate);
}
