using Microsoft.Extensions.Logging;
using Percolator.Application.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;
using Percolator.Identity;

namespace Percolator.Infrastructure.Chat;

/// <summary>
/// Service for managing delivery certificates for local self identities.
/// Certificates are generated JIT on demand without persistence.
/// </summary>
public sealed class SelfCertificateService : ISelfCertificateService
{
    private readonly ISelfIdentityQueries _selfIdentityQueries;
    private readonly ILocalIdentitySigner _localIdentitySigner;
    private readonly ILogger<SelfCertificateService> _logger;
    private readonly TimeProvider _timeProvider;

    public SelfCertificateService(
        ISelfIdentityQueries selfIdentityQueries,
        ILocalIdentitySigner localIdentitySigner,
        ILogger<SelfCertificateService> logger,
        TimeProvider timeProvider)
    {
        _selfIdentityQueries = selfIdentityQueries;
        _localIdentitySigner = localIdentitySigner;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<DeliveryCertificate?> GenerateValidCertAsync(ChatSelfId selfId, CancellationToken ct)
    {
        // Get the self identity crypto info
        var identitySelfId = new SelfId(selfId.Value);
        var selfIdentity = await _selfIdentityQueries.GetSelfIdentityCertInfoAsync(identitySelfId, ct);
        if (selfIdentity is null)
        {
            _logger.LogWarning("Self identity not found for SelfId {SelfId}", selfId);
            return null;
        }

        // Get the active identity fingerprint
        if (selfIdentity.Value.Fingerprint is null)
        {
            _logger.LogWarning("No active identity fingerprint found for SelfId {SelfId}", selfId);
            return null;
        }

        // Always generate a new certificate (no persistence for self-generated certificates)
        _logger.LogInformation("Generating new certificate for SelfId {SelfId}", selfId);

        // Generate certificate payload with 24-hour expiration
        var expiration = _timeProvider.GetUtcNow().AddHours(24);
        var payload = DeliveryCertificateWireFormatter.Pack(selfIdentity.Value.Fingerprint.Span, expiration);

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
}
