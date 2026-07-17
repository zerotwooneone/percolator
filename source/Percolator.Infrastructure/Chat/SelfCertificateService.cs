using Microsoft.Extensions.Logging;
using Percolator.Application.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;
using Percolator.Cryptography;
using Percolator.Identity;
using PublicIdentityId = Percolator.Identity.PublicIdentityId;

namespace Percolator.Infrastructure.Chat;

/// <summary>
/// Service for managing delivery certificates for local self identities.
/// Certificates are generated JIT on demand without persistence.
/// </summary>
public sealed class SelfCertificateService : ISelfCertificateService
{
    private readonly ILocalIdentitySigner _localIdentitySigner;
    private readonly ILogger<SelfCertificateService> _logger;
    private readonly TimeProvider _timeProvider;

    public SelfCertificateService(
        ILocalIdentitySigner localIdentitySigner,
        ILogger<SelfCertificateService> logger,
        TimeProvider timeProvider)
    {
        _localIdentitySigner = localIdentitySigner;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<DeliveryCertificate?> GenerateValidCertAsync(ChatSelfId selfId, PublicIdentityId publicIdentityId, RatchetIdentityKey fingerprint, CancellationToken ct)
    {
        // Always generate a new certificate (no persistence for self-generated certificates)
        _logger.LogInformation("Generating new certificate for SelfId {SelfId}", selfId);

        // Generate certificate payload with 24-hour expiration
        var expiration = _timeProvider.GetUtcNow().AddHours(24);
        var payload = DeliveryCertificateWireFormatter.Pack(fingerprint.Span, expiration);

        // Sign the payload with the relay's root key
        var signature = await _localIdentitySigner.SignWithRelayRootKeyAsync(payload, ct);

        // Create the certificate
        var certificate = new DeliveryCertificate(
            DeliveryCertificatePayloadBytes.FromBytes(payload),
            SignatureBytes.FromSpan(signature.Span),
            expiration);

        _logger.LogInformation("Successfully generated certificate for SelfId {SelfId}, expires at {ExpiresAt}", selfId, expiration);
        return certificate;
    }

    public async Task<RelayAuthenticationHeaders?> GetRelayAuthenticationHeadersAsync(ChatSelfId selfId, PublicIdentityId publicIdentityId, CancellationToken ct)
    {
        // Generate timestamp
        var timestamp = _timeProvider.GetUtcNow();
        var timestampString = timestamp.ToUnixTimeSeconds().ToString();

        // Create signature payload
        var signaturePayload = System.Text.Encoding.UTF8.GetBytes($"{publicIdentityId.Value.ToString("N")}:{timestamp.ToUnixTimeSeconds()}");
        var signature = await _localIdentitySigner.SignWithRelayRootKeyAsync(signaturePayload, ct);
        var signatureString = Convert.ToBase64String(signature.Span);

        // Return authentication headers
        return new RelayAuthenticationHeaders(
            publicIdentityId.Value.ToString("N"),
            timestampString,
            signatureString);
    }
}
