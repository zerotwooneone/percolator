using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Ports;

/// <summary>
/// Persists transfer-scoped ephemeral ECDSA P-256 certificate/keypairs inside the encrypted
/// SQLCipher vault for the lifetime of a transfer session so pause/resume and offline-author
/// seeding preserve E2E-pinned SHA-256 fingerprints.
/// </summary>
public interface ITransferSessionCertificateStore
{
    ValueTask<TransferSessionCertificateMaterial> GetOrCreateAsync(
        TransferSessionId sessionId,
        CancellationToken ct = default);

    ValueTask<TransferSessionCertificateMaterial?> GetAsync(
        TransferSessionId sessionId,
        CancellationToken ct = default);

    ValueTask DeleteAsync(
        TransferSessionId sessionId,
        CancellationToken ct = default);
}
