using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using Percolator.Identity;
using IdentityPeerId = Percolator.Identity.PeerId;
using Percolator.Network;
using NetworkPeerId = Percolator.Network.PeerId;

namespace Percolator.Application.Network;

public class GrpcClientFactory : IGrpcClientFactory
{
    private readonly ILogger<GrpcClientFactory> _logger;
    private readonly IPeerRepository _peerRepository;
    private readonly IPeerConnectionRepository _peerConnectionRepository;

    public GrpcClientFactory(ILogger<GrpcClientFactory> logger, IPeerRepository peerRepository, IPeerConnectionRepository peerConnectionRepository)
    {
        _logger = logger;
        _peerRepository = peerRepository;
        _peerConnectionRepository = peerConnectionRepository;
    }

    public TransportService.TransportServiceClient CreateClient(
        DnsEndPoint address, 
        string peerName,
        X509Certificate2 clientCertificate)
    {
        var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(clientCertificate);
        handler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
        {
            _logger.LogInformation("Client: Performing server certificate validation for subject '{Subject}' issued by '{Issuer}'.", cert?.Subject, cert?.Issuer);
            if (cert is null)
            {
                _logger.LogError("Client: Server certificate validation failed for peer '{PeerName}'. Reason: Certificate is null.", peerName);
                return false;
            }

            // We expect chain errors because the certificates are self-signed.
            if (errors != SslPolicyErrors.None && errors != SslPolicyErrors.RemoteCertificateChainErrors)
            {
                _logger.LogError("Client: Server certificate validation failed for peer '{PeerName}'. Reason: SSL policy errors: {SslPolicyErrors}", peerName, errors);
                return false;
            }

            var peer = _peerRepository.GetByNameAsync(peerName).GetAwaiter().GetResult();
            var peerConnection = peer is null ? null : _peerConnectionRepository.GetByIdAsync(new NetworkPeerId(peer.Id.Value)).GetAwaiter().GetResult();

            if (peer is null || peerConnection is null)
            {
                _logger.LogWarning("Client: Peer '{PeerName}' is unknown. Trusting on first use (TOFU).", peerName);
                var newPeer = peer ?? new Peer(new IdentityPeerId(Guid.NewGuid()), peerName);
                _peerRepository.AddAsync(newPeer).GetAwaiter().GetResult();

                var newConnection = new PeerConnection(
                    new NetworkPeerId(newPeer.Id.Value),
                    null, 
                    new[] { new GrpcEndPoint(address, DateTimeOffset.UtcNow) },
                    new[] { new TlsCertificate(cert.Export(X509ContentType.Cert)) },
                    DateTimeOffset.UtcNow);
                _peerConnectionRepository.SaveAsync(newConnection).GetAwaiter().GetResult();
                return true;
            }

            var expectedCertificate = X509CertificateLoader.LoadCertificate(peerConnection.TlsCertificates.First().Value);
            if (cert.Equals(expectedCertificate))
            {
                _logger.LogInformation("Client: Server certificate for peer '{PeerName}' is trusted.", peerName);
                return true;
            }

            _logger.LogError("Client: Server certificate validation failed for peer '{PeerName}'. Reason: Presented certificate with thumbprint {Thumbprint} does not match the expected certificate with thumbprint {ExpectedThumbprint}.", 
                peerName, cert.Thumbprint, expectedCertificate.Thumbprint);
            return false;
        };

        var channel = GrpcChannel.ForAddress($"https://{address.Host}:{address.Port}", new GrpcChannelOptions { HttpHandler = handler });
        return new TransportService.TransportServiceClient(channel);
    }
}
