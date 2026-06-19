using Grpc.Core;
using Microsoft.Extensions.Logging;
using Percolator.Application.Chat;
using Percolator.Contracts;
using Percolator.Cryptography;
using System.Net;
using Percolator.Chat.GroupLedger;

namespace Percolator.Infrastructure.Network.Grpc;

public sealed class RelayTransportClient : IRelayTransportClient
{
    private readonly IPeerGrpcChannelFactory _channelFactory;
    private readonly ILogger<RelayTransportClient> _logger;

    public RelayTransportClient(
        IPeerGrpcChannelFactory channelFactory,
        ILogger<RelayTransportClient> logger)
    {
        _channelFactory = channelFactory;
        _logger = logger;
    }

    public async Task<Percolator.Chat.GroupLedger.DeliveryCertificate> FetchCertificateAsync(
        string targetHost,
        int targetPort,
        string senderPkh,
        DateTimeOffset timestamp,
        Signature signature,
        CancellationToken ct)
    {
        // Create channel using the centralized factory
        var channel = _channelFactory.CreateChannel(new DnsEndPoint(targetHost, targetPort));
        var client = new TransportService.TransportServiceClient(channel);

        // Construct metadata headers
        var headers = new Metadata();
        headers.Add("x-percolator-sender-pkh", senderPkh);
        headers.Add("x-percolator-timestamp", timestamp.ToUnixTimeSeconds().ToString());
        headers.Add("x-percolator-signature", Google.Protobuf.ByteString.CopyFrom(signature.Span).ToBase64());

        // Construct call options with metadata and cancellation token
        var callOptions = new CallOptions(headers: headers, cancellationToken: ct);

        _logger.LogInformation("Fetching delivery certificate from relay {Host}:{Port} for PKH {Pkh}", targetHost, targetPort, senderPkh);

        // Dispatch the request
        var response = await client.GetDeliveryCertificateAsync(new GetDeliveryCertificateRequest(), callOptions);

        _logger.LogInformation("Successfully fetched delivery certificate from relay {Host}:{Port}", targetHost, targetPort);

        // Parse the protobuf response directly into rich domain types
        var certificateData = response.Certificate.CertificateData.ToByteArray();
        var payload = DeliveryCertificatePayloadBytes.FromBytes(certificateData);
        var relaySig = SignatureBytes.FromBytes(response.Certificate.Signature.ToByteArray());

        // Safe extraction with guaranteed bounds checking
        var expiresAt = Percolator.Application.Chat.DeliveryCertificateWireFormatter.ExtractExpiration(payload.Span);

        return new Percolator.Chat.GroupLedger.DeliveryCertificate(payload, relaySig, expiresAt);
    }
}
