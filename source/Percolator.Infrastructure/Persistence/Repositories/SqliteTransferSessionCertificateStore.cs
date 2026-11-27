namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Common;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;

/// <summary>
/// Persists transfer-scoped ephemeral ECDSA P-256 certificate/keypairs inside the SQLCipher vault
/// so pause/resume and offline-author seeding preserve the exact E2E-pinned SHA-256 fingerprint,
/// and erases key material upon transfer removal.
/// </summary>
public sealed class SqliteTransferSessionCertificateStore : ITransferSessionCertificateStore
{
    private readonly IDbContextFactory<PercolatorDbContext> _dbContextFactory;
    private readonly IDateTimeProvider _clock;

    public SqliteTransferSessionCertificateStore(
        IDbContextFactory<PercolatorDbContext> dbContextFactory,
        IDateTimeProvider clock)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask<TransferSessionCertificateMaterial> GetOrCreateAsync(
        TransferSessionId sessionId,
        CancellationToken ct = default)
    {
        await using PercolatorDbContext db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        TransferSessionCertificateEntity? existing = await db.TransferSessionCertificates
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TransferId == sessionId.Value, ct)
            .ConfigureAwait(false);

        if (existing != null &&
            existing.CertSha256.Length == 32 &&
            existing.CertificateDer.Length > 0 &&
            existing.PrivateKeyPkcs8.Length > 0)
        {
            return new TransferSessionCertificateMaterial(
                CertSha256Fingerprint.FromBytes(existing.CertSha256),
                existing.CertificateDer.ToArray(),
                existing.PrivateKeyPkcs8.ToArray());
        }

        TransferSessionCertificateMaterial generated = GenerateEphemeralP256Certificate(sessionId, _clock.UtcNow);
        byte[] certDerCopy = generated.CertificateDer.ToArray();
        byte[] pkcs8Copy = generated.PrivateKeyPkcs8.ToArray();

        try
        {
            var entity = new TransferSessionCertificateEntity
            {
                TransferId = sessionId.Value,
                CertSha256 = generated.Fingerprint.Span.ToArray(),
                CertificateDer = certDerCopy,
                PrivateKeyPkcs8 = pkcs8Copy,
                CreatedAtUtc = _clock.UtcNow
            };

            db.TransferSessionCertificates.Add(entity);
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return generated;
            }
            catch (DbUpdateException)
            {
                // Concurrent creation race: re-read winning row and zeroize discarded key material
                generated.Dispose();
                TransferSessionCertificateEntity? winner = await db.TransferSessionCertificates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.TransferId == sessionId.Value, ct)
                    .ConfigureAwait(false);

                if (winner == null)
                {
                    throw;
                }

                return new TransferSessionCertificateMaterial(
                    CertSha256Fingerprint.FromBytes(winner.CertSha256),
                    winner.CertificateDer.ToArray(),
                    winner.PrivateKeyPkcs8.ToArray());
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8Copy);
        }
    }

    public async ValueTask<TransferSessionCertificateMaterial?> GetAsync(
        TransferSessionId sessionId,
        CancellationToken ct = default)
    {
        await using PercolatorDbContext db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        TransferSessionCertificateEntity? existing = await db.TransferSessionCertificates
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TransferId == sessionId.Value, ct)
            .ConfigureAwait(false);

        if (existing == null ||
            existing.CertSha256.Length != 32 ||
            existing.CertificateDer.Length == 0 ||
            existing.PrivateKeyPkcs8.Length == 0)
        {
            return null;
        }

        return new TransferSessionCertificateMaterial(
            CertSha256Fingerprint.FromBytes(existing.CertSha256),
            existing.CertificateDer.ToArray(),
            existing.PrivateKeyPkcs8.ToArray());
    }

    public async ValueTask DeleteAsync(
        TransferSessionId sessionId,
        CancellationToken ct = default)
    {
        await using PercolatorDbContext db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        TransferSessionCertificateEntity? existing = await db.TransferSessionCertificates
            .FirstOrDefaultAsync(c => c.TransferId == sessionId.Value, ct)
            .ConfigureAwait(false);

        if (existing == null)
        {
            return;
        }

        if (existing.PrivateKeyPkcs8.Length > 0)
        {
            CryptographicOperations.ZeroMemory(existing.PrivateKeyPkcs8);
        }

        db.TransferSessionCertificates.Remove(existing);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    internal static TransferSessionCertificateMaterial GenerateEphemeralP256Certificate(
        TransferSessionId sessionId,
        DateTimeOffset nowUtc)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var subject = new X500DistinguishedName($"CN=percolator-ft-{sessionId.Value:N}");
        var request = new CertificateRequest(subject, ecdsa, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature,
            critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                new("1.3.6.1.5.5.7.3.1"), // Server Authentication
                new("1.3.6.1.5.5.7.3.2")  // Client Authentication
            },
            critical: false));

        using X509Certificate2 cert = request.CreateSelfSigned(
            nowUtc.AddMinutes(-5),
            nowUtc.AddDays(365));

        byte[] certDer = cert.Export(X509ContentType.Cert);
        byte[] privateKeyPkcs8 = ecdsa.ExportPkcs8PrivateKey();
        CertSha256Fingerprint fingerprint = CertSha256Fingerprint.ComputeFromCertificateDer(certDer);

        return new TransferSessionCertificateMaterial(fingerprint, certDer, privateKeyPkcs8);
    }
}
