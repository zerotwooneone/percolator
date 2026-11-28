// SPECIFICATION GOVERNANCE: Read Percolator.Infrastructure/spec/repository.spec.md before modifying this file.

namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteTransferSessionCheckpointStore : ITransferSessionCheckpointStore
{
    private readonly IDbContextFactory<PercolatorDbContext> _dbContextFactory;

    public SqliteTransferSessionCheckpointStore(IDbContextFactory<PercolatorDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    public async ValueTask SaveCheckpointAsync(
        TransferSessionId sessionId,
        Bitfield localBitfield,
        TransferSessionState state,
        TotalBundleBytes uploadedBytes,
        DateTimeOffset? completedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(localBitfield);

        await using PercolatorDbContext db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        TransferSessionEntity? session = await db.TransferSessions
            .FirstOrDefaultAsync(s => s.TransferId == sessionId.Value, ct)
            .ConfigureAwait(false);

        if (session == null)
        {
            return;
        }

        session.State = (int)state;
        session.UploadedSizeBytes = uploadedBytes.Bytes;
        session.CompletedAtUtc = completedAtUtc;

        TransferBitfieldEntity? bitfieldEntity = await db.TransferBitfields
            .FirstOrDefaultAsync(b => b.TransferId == sessionId.Value, ct)
            .ConfigureAwait(false);

        if (bitfieldEntity == null)
        {
            bitfieldEntity = new TransferBitfieldEntity
            {
                TransferId = sessionId.Value
            };
            db.TransferBitfields.Add(bitfieldEntity);
        }

        bitfieldEntity.BitfieldBytes = localBitfield.ToByteArray();
        bitfieldEntity.VerifiedPieces = localBitfield.PopCount;
        bitfieldEntity.TotalPieces = localBitfield.TotalChunks.Count;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
