using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Percolator.Application.Chat;
using Percolator.Chat;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Infrastructure.Network;
using System.Net;
using Google.Protobuf;
using ProtobufDeliveryCertificate = Percolator.Contracts.DeliveryCertificate;

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

    public async Task<Percolator.Chat.DeliveryCertificate> FetchCertificateAsync(
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
        var relaySig = Signature.FromBytes(response.Certificate.Signature.ToByteArray());

        // Read expiration invariants directly from the domain concept wrapper
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(BitConverter.ToInt64(payload.Span.Slice(16, 8)));

        return new Percolator.Chat.DeliveryCertificate(payload, relaySig, expiresAt);
    }
}
