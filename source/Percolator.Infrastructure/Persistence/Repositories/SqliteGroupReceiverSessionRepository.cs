namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Application2.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteGroupReceiverSessionRepository : IGroupReceiverSessionRepository
{
    private readonly PercolatorDbContext _dbContext;

    public SqliteGroupReceiverSessionRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<GroupReceiverSession?> GetReceiverSessionAsync(
        ChannelId channelId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        CancellationToken ct = default)
    {
        var channelGuid = channelId.Value;
        var authorGuid = authorId.Value;

        var entity = await _dbContext.VaultGroupReceiverSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.ChannelId == channelGuid &&
                     s.AuthorIdentityId == authorGuid &&
                     s.AuthorDeviceId == authorDeviceId.Value,
                ct);

        if (entity == null || entity.ChainKeyCiphertext.Length == 0)
        {
            return null;
        }

        using var chainKey = ChainKey.FromSpan(entity.ChainKeyCiphertext);
        var signingKey = entity.AuthorSigningPub != null && entity.AuthorSigningPub.Length > 0
            ? IdentityKey.FromSpan(entity.AuthorSigningPub)
            : null;

        var skippedKeys = DeserializeSkippedKeys(entity.SkippedKeysBlob);

        return GroupReceiverSession.Rehydrate(
            channelId,
            authorId,
            authorDeviceId,
            chainKey,
            entity.ReceivingCounter,
            entity.KeyId,
            signingKey,
            skippedKeys: skippedKeys);
    }

    public async Task SaveReceiverSessionAsync(GroupReceiverSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var channelGuid = session.ChannelId.Value;
        var authorGuid = session.AuthorId.Value;

        var existing = await _dbContext.VaultGroupReceiverSessions
            .FirstOrDefaultAsync(
                s => s.ChannelId == channelGuid &&
                     s.AuthorIdentityId == authorGuid &&
                     s.AuthorDeviceId == session.AuthorDeviceId.Value,
                ct);

        var snapshot = session.ExportSnapshot();
        var chainBytes = snapshot.ChainKeyBytes;
        var signingPubBytes = snapshot.AuthorSigningKey != null ? snapshot.AuthorSigningKey.Span.ToArray() : null;
        var skippedBlob = SerializeSkippedKeys(snapshot.SkippedMessageKeys);

        if (existing == null)
        {
            var entity = new VaultGroupReceiverSessionEntity
            {
                ChannelId = channelGuid,
                AuthorIdentityId = authorGuid,
                AuthorDeviceId = session.AuthorDeviceId.Value,
                KeyId = session.KeyId,
                AuthorSigningPub = signingPubBytes,
                ChainKeyCiphertext = chainBytes,
                ReceivingCounter = session.ReceivingCounter,
                SkippedKeysBlob = skippedBlob,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _dbContext.VaultGroupReceiverSessions.AddAsync(entity, ct);
        }
        else
        {
            existing.KeyId = session.KeyId;
            existing.AuthorSigningPub = signingPubBytes;
            existing.ChainKeyCiphertext = chainBytes;
            existing.ReceivingCounter = session.ReceivingCounter;
            existing.SkippedKeysBlob = skippedBlob;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    private static byte[]? SerializeSkippedKeys(IReadOnlyDictionary<uint, byte[]> skippedKeys)
    {
        if (skippedKeys == null || skippedKeys.Count == 0) return null;

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(skippedKeys.Count);
        foreach (var (counter, keyBytes) in skippedKeys)
        {
            writer.Write(counter);
            writer.Write(keyBytes.Length);
            writer.Write(keyBytes);
        }
        return ms.ToArray();
    }

    private static List<KeyValuePair<uint, MessageKey>>? DeserializeSkippedKeys(byte[]? blob)
    {
        if (blob == null || blob.Length < 4) return null;

        using var ms = new MemoryStream(blob);
        using var reader = new BinaryReader(ms);
        int count = reader.ReadInt32();
        var list = new List<KeyValuePair<uint, MessageKey>>(count);
        for (int i = 0; i < count; i++)
        {
            uint counter = reader.ReadUInt32();
            int len = reader.ReadInt32();
            byte[] keyBytes = reader.ReadBytes(len);
            list.Add(new KeyValuePair<uint, MessageKey>(counter, MessageKey.FromSpan(keyBytes)));
        }
        return list;
    }
}
