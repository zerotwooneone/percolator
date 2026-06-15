using Microsoft.Extensions.Logging;
using Percolator.Application.Chat;
using Percolator.Chat;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using Percolator.Network.Messaging;

namespace Percolator.Application.Apps.Chat;

public sealed class CertificateOrchestrator : ICertificateOrchestrator
{
    private readonly IDeliveryCertificateStore _certificateStore;
    private readonly ISelfIdentityRepository _selfIdentityRepository;
    private readonly ILocalIdentitySigner _localIdentitySigner;
    private readonly IRelayTopology _relayTopology;
    private readonly IPeerRoutingProfileRepository _peerRoutingProfileRepository;
    private readonly IRelayTransportClient _relayTransportClient;
    private readonly ILogger<CertificateOrchestrator> _logger;

    public CertificateOrchestrator(
        IDeliveryCertificateStore certificateStore,
        ISelfIdentityRepository selfIdentityRepository,
        ILocalIdentitySigner localIdentitySigner,
        IRelayTopology relayTopology,
        IPeerRoutingProfileRepository peerRoutingProfileRepository,
        IRelayTransportClient relayTransportClient,
        ILogger<CertificateOrchestrator> logger)
    {
        _certificateStore = certificateStore;
        _selfIdentityRepository = selfIdentityRepository;
        _localIdentitySigner = localIdentitySigner;
        _relayTopology = relayTopology;
        _peerRoutingProfileRepository = peerRoutingProfileRepository;
        _relayTransportClient = relayTransportClient;
        _logger = logger;
    }

    public async Task RefreshLocalCertificateAsync(CancellationToken ct)
    {
        // 1. Resolve Relay Destination
        // Get the local identity
        var selfIdentity = await _selfIdentityRepository.GetMostRecentAsync(ct).ConfigureAwait(false);
        if (selfIdentity is null)
        {
            _logger.LogWarning("No self identity found for certificate refresh");
            return;
        }

        // Call the topology layer to resolve the designated relay peer
        var networkPeerId = new Percolator.Network.PeerId(selfIdentity.PeerId.Value);
        var relayPeerId = await _relayTopology.GetRelayForAsync(networkPeerId, ct).ConfigureAwait(false);
        if (relayPeerId is null)
        {
            _logger.LogWarning("No relay configured for certificate refresh");
            return;
        }

        // Use the repository to extract its active endpoint network profile
        var relayProfile = await _peerRoutingProfileRepository.GetByIdAsync(relayPeerId, ct).ConfigureAwait(false);
        if (relayProfile is null)
        {
            _logger.LogWarning("No routing profile found for relay {RelayPeerId}", relayPeerId);
            return;
        }

        // Pull the target host string and port integer from the profile
        var firstEndpoint = relayProfile.Endpoints.FirstOrDefault();
        if (firstEndpoint is null)
        {
            _logger.LogWarning("No endpoints found for relay {RelayPeerId}", relayPeerId);
            return;
        }
        var targetHost = firstEndpoint.EndPoint.Host;
        var targetPort = firstEndpoint.EndPoint.Port;

        // 2. Generate Signature Challenge
        // Generate the current UTC timestamp
        var timestamp = DateTimeOffset.UtcNow;

        // Extract the cryptographic public key hash fingerprint token (Respecting Rule 6)
        var activeKey = selfIdentity.GetActiveKey(timestamp);
        if (activeKey is null)
        {
            _logger.LogWarning("No active signing key found for local identity profile.");
            return;
        }
        var localPkh = Convert.ToBase64String(activeKey.Fingerprint);

        // Combine parameters into a uniform challenge buffer payload
        var payload = System.Text.Encoding.UTF8.GetBytes($"{localPkh}{timestamp.ToUnixTimeSeconds()}");
        var signature = await _localIdentitySigner.SignWithLocalIdentityKeyAsync(payload, ct).ConfigureAwait(false);

        // 3. Execute Transport Call
        // Call IRelayTransportClient to fetch the certificate
        var certificate = await _relayTransportClient.FetchCertificateAsync(
            targetHost,
            targetPort,
            localPkh,
            timestamp,
            signature,
            ct).ConfigureAwait(false);

        // 4. Store Result
        // Save the DeliveryCertificate to IDeliveryCertificateStore
        _certificateStore.SetCertificate(certificate);

        _logger.LogInformation("Successfully refreshed delivery certificate, expires at {ExpiresAt}", certificate.ExpiresAt);
    }
}
