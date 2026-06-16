using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Percolator.Cryptography;

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
        string senderPkh,
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

        // Look up the peer's public key using the PKH lookup string
        var publicKey = await _peerIdentityQueries.GetPublicKeyByPkhAsync(senderPkh, ct);
        if (publicKey is null)
        {
            _logger.LogWarning("Peer not found for PKH: {Pkh}", senderPkh);
            return false;
        }

        // Instantiate ECDsa public key context from the retrieved RatchetIdentityKey parameters
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(publicKey.Span, out _);

        // Verify the inbound Signature payload using SHA256
        // The signature is over the combined payload [senderPkh + timestamp]
        var payload = System.Text.Encoding.UTF8.GetBytes($"{senderPkh}{requestTimestamp.ToUnixTimeSeconds()}");
        var isValid = ecdsa.VerifyData(payload, signature.Span, HashAlgorithmName.SHA256);

        if (!isValid)
        {
            _logger.LogWarning("Signature verification failed for PKH: {Pkh}", senderPkh);
        }

        return isValid;
    }
}
