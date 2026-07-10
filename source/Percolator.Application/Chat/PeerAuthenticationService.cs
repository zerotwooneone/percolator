using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Chat;

public sealed class PeerAuthenticationService : IPeerAuthenticationService
{
    private readonly IPeerIdentityQueries _peerIdentityQueries;
    private readonly ILogger<PeerAuthenticationService> _logger;
    private readonly TimeProvider _timeProvider;

    public PeerAuthenticationService(
        IPeerIdentityQueries peerIdentityQueries,
        ILogger<PeerAuthenticationService> logger,
        TimeProvider timeProvider)
    {
        _peerIdentityQueries = peerIdentityQueries;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<bool> AuthenticateDeliveryCertificateRequestAsync(
        PublicIdentityId senderPublicIdentityId,
        DateTimeOffset requestTimestamp,
        Signature signature,
        CancellationToken ct)
    {
        // Reject if requestTimestamp is older than 60 seconds (Replay attack prevention)
        var now = _timeProvider.GetUtcNow();
        if (now - requestTimestamp > TimeSpan.FromSeconds(60))
        {
            _logger.LogWarning("Certificate request timestamp is too old: {Timestamp}", requestTimestamp);
            return false;
        }

        // Look up the peer's public key using the PublicIdentityId
        var publicKey = await _peerIdentityQueries.GetPublicKeyByPublicIdentityIdAsync(senderPublicIdentityId, ct).ConfigureAwait(false);
        if (publicKey is null)
        {
            _logger.LogWarning("Peer not found for PublicIdentityId: {PublicIdentityId}", senderPublicIdentityId);
            return false;
        }

        // Instantiate ECDsa public key context from the retrieved RatchetIdentityKey parameters
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(publicKey.Span, out _);

        // Verify the inbound Signature payload using SHA256
        // The signature is over the combined payload [senderPublicIdentityId + timestamp]
        var payload = System.Text.Encoding.UTF8.GetBytes($"{senderPublicIdentityId}{requestTimestamp.ToUnixTimeSeconds()}");
        var isValid = ecdsa.VerifyData(payload, signature.Span, HashAlgorithmName.SHA256);

        if (!isValid)
        {
            _logger.LogWarning("Signature verification failed for PublicIdentityId: {PublicIdentityId}", senderPublicIdentityId);
        }

        return isValid;
    }
}
