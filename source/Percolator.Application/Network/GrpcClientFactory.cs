using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using Percolator.Network;

namespace Percolator.Application.Network;

public class GrpcClientFactory : IGrpcClientFactory
{
    private readonly ILogger<GrpcClientFactory> _logger;

    public GrpcClientFactory(ILogger<GrpcClientFactory> logger)
    {
        _logger = logger;
    }

    public TransportService.TransportServiceClient CreateClient(
        DnsEndPoint endpoint, 
        X509Certificate2 clientCertificate,
        TlsCertificate? tlsCertificate)
    {
        var channel = CreateChannel(endpoint, clientCertificate, tlsCertificate);
        return new TransportService.TransportServiceClient(channel);
    }

    private GrpcChannel CreateChannel(DnsEndPoint endpoint, X509Certificate2 clientCertificate, TlsCertificate? tlsCertificate = null)
    {
        var address = $"https://{endpoint.Host}:{endpoint.Port}";
        var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(clientCertificate);
        handler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
        {
            _logger.LogInformation("Performing custom server certificate validation. SSL Policy Errors: {SslPolicyErrors}", errors);

            if (cert is null)
            {
                _logger.LogWarning("Server certificate is null. Validation failed.");
                return false;
            }

            _logger.LogInformation("Received server certificate. Subject: {Subject}, Thumbprint: {Thumbprint}", cert.Subject, cert.Thumbprint);

            if (errors != System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors && errors != System.Net.Security.SslPolicyErrors.None)
            {
                _logger.LogWarning("SSL policy reported errors other than chain trust: {SslPolicyErrors}", errors);
            }

            var identityExtension = cert.Extensions[Percolator.Cryptography.Oids.PeerIdentityKey];
            if (identityExtension is null)
            {
                _logger.LogError("Certificate does not contain the required peer identity extension (OID: {Oid}). Validation failed.", Percolator.Cryptography.Oids.PeerIdentityKey);
                return false;
            }

            _logger.LogInformation("Found peer identity extension. Validating content.");

            try
            {
                var asnReader = new AsnReader(identityExtension.RawData, AsnEncodingRules.BER);
                var actualPublicKey = asnReader.ReadOctetString();

                if (asnReader.HasData)
                {
                    _logger.LogWarning("ASN.1 reader has extra data after reading the OCTET STRING. The data may be malformed.");
                }

                if (tlsCertificate is not null)
                {
                    var validationResult = actualPublicKey.SequenceEqual(tlsCertificate.Value);
                    if (validationResult)
                    {
                        _logger.LogInformation("Public key in certificate matches expected public key. Validation successful.");
                        return true;
                    }
                    
                    _logger.LogError("Public key in certificate does NOT match expected public key. Validation failed.");
                    _logger.LogDebug("Expected Key (Base64): {ExpectedKey}", Convert.ToBase64String(tlsCertificate.Value));
                    _logger.LogDebug("Actual Key (Base64): {ActualKey}", Convert.ToBase64String(actualPublicKey));
                    return false;
                }
                
                // TOFU: Trust on first use. If no certificate was provided, we accept the one from the server.
                _logger.LogInformation("No expected public key provided. Trusting the key from the certificate on first use.");
                return true;
            }
            catch (AsnContentException e)
            {
                _logger.LogError(e, "Failed to parse ASN.1 content from certificate extension. Validation failed.");
                return false;
            }
        };

        return GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
    }
}
