namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Application2.Ingress;
using Percolator.Application2.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqlitePendingHandshakeRepository : IPendingHandshakeRepository
{
    private readonly PercolatorDbContext _dbContext;

    public SqlitePendingHandshakeRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task SavePendingHandshakeAsync(PublicIdentityId recipientId, PublicIdentityId senderId, InboundHandshakeEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var recipientGuid = recipientId.Value;
        var senderGuid = senderId.Value;
        var payloadBytes = SerializeEnvelope(envelope);

        var existing = await _dbContext.VaultPendingHandshakes
            .FirstOrDefaultAsync(h => h.RecipientIdentityId == recipientGuid && h.SenderIdentityId == senderGuid, ct);

        if (existing == null)
        {
            var entity = new VaultPendingHandshakeEntity
            {
                RecipientIdentityId = recipientGuid,
                SenderIdentityId = senderGuid,
                EnvelopePayload = payloadBytes,
                CreatedAtUtc = envelope.ReceivedAtUtc
            };
            await _dbContext.VaultPendingHandshakes.AddAsync(entity, ct);
        }
        else
        {
            existing.EnvelopePayload = payloadBytes;
            existing.CreatedAtUtc = envelope.ReceivedAtUtc;
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<InboundHandshakeEnvelope?> GetPendingHandshakeAsync(PublicIdentityId recipientId, PublicIdentityId senderId, CancellationToken ct = default)
    {
        var recipientGuid = recipientId.Value;
        var senderGuid = senderId.Value;

        var entity = await _dbContext.VaultPendingHandshakes
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.RecipientIdentityId == recipientGuid && h.SenderIdentityId == senderGuid, ct);

        if (entity == null || entity.EnvelopePayload.Length == 0)
        {
            return null;
        }

        return DeserializeEnvelope(recipientId, senderId, entity.EnvelopePayload);
    }

    public async Task DeletePendingHandshakeAsync(PublicIdentityId recipientId, PublicIdentityId senderId, CancellationToken ct = default)
    {
        var recipientGuid = recipientId.Value;
        var senderGuid = senderId.Value;

        var existing = await _dbContext.VaultPendingHandshakes
            .FirstOrDefaultAsync(h => h.RecipientIdentityId == recipientGuid && h.SenderIdentityId == senderGuid, ct);

        if (existing != null)
        {
            _dbContext.VaultPendingHandshakes.Remove(existing);
            await _dbContext.SaveChangesAsync(ct);
        }
    }

    private static byte[] SerializeEnvelope(InboundHandshakeEnvelope env)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(env.SenderDeviceId.Value);
        writer.Write(env.SenderIdentityKey.Span);
        writer.Write(env.SenderEphemeralKey.Span);
        writer.Write(env.SignedPreKeyId);
        writer.Write(env.OneTimePreKeyId.HasValue);
        if (env.OneTimePreKeyId.HasValue)
        {
            writer.Write(env.OneTimePreKeyId.Value);
        }

        writer.Write(env.EncryptedPayload.Length);
        writer.Write(env.EncryptedPayload.Span);
        writer.Write(env.ReceivedAtUtc.ToUnixTimeMilliseconds());

        return ms.ToArray();
    }

    private static InboundHandshakeEnvelope DeserializeEnvelope(PublicIdentityId recipientId, PublicIdentityId senderId, byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var deviceId = new DeviceId(reader.ReadUInt32());
        var identityKey = IdentityKey.FromSpan(reader.ReadBytes(32));
        var ephemeralKey = DhPublicKey.FromSpan(reader.ReadBytes(32));
        var signedPreKeyId = reader.ReadUInt32();
        bool hasOpk = reader.ReadBoolean();
        uint? opkId = hasOpk ? reader.ReadUInt32() : null;

        int payloadLen = reader.ReadInt32();
        byte[] payloadBytes = reader.ReadBytes(payloadLen);
        long unixMs = reader.ReadInt64();
        var receivedAt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);

        return new InboundHandshakeEnvelope(
            recipientId,
            senderId,
            deviceId,
            identityKey,
            ephemeralKey,
            signedPreKeyId,
            opkId,
            payloadBytes,
            receivedAt);
    }
}
