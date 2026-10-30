namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteRatchetSessionRepository : IRatchetSessionRepository
{
    private readonly PercolatorDbContext _dbContext;

    public SqliteRatchetSessionRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<DirectRatchetSession?> GetSessionAsync(
        PublicIdentityId ownerId,
        PublicIdentityId remotePeerId,
        DeviceId remoteDeviceId,
        CancellationToken ct = default)
    {
        var ownerGuid = ownerId.Value;
        var remoteGuid = remotePeerId.Value;

        var entity = await _dbContext.VaultRatchetSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.OwnerIdentityId == ownerGuid &&
                     s.RemotePeerId == remoteGuid &&
                     s.RemoteDeviceId == remoteDeviceId.Value,
                ct);

        if (entity == null)
        {
            return null;
        }

        var rootKey = entity.RootKeyCiphertext.Length > 0 ? ChainKey.FromSpan(entity.RootKeyCiphertext) : null;
        var sendKey = entity.SendChainKeyCiphertext != null && entity.SendChainKeyCiphertext.Length > 0 ? ChainKey.FromSpan(entity.SendChainKeyCiphertext) : null;
        var recvKey = entity.RecvChainKeyCiphertext != null && entity.RecvChainKeyCiphertext.Length > 0 ? ChainKey.FromSpan(entity.RecvChainKeyCiphertext) : null;
        var localPriv = entity.LocalEphemeralPrivCiphertext != null && entity.LocalEphemeralPrivCiphertext.Length > 0 ? EphemeralPrivateKey.FromSpan(entity.LocalEphemeralPrivCiphertext) : null;
        var localPub = entity.LocalEphemeralPub != null && entity.LocalEphemeralPub.Length > 0 ? DhPublicKey.FromSpan(entity.LocalEphemeralPub) : null;
        var remotePub = entity.RemoteEphemeralPub != null && entity.RemoteEphemeralPub.Length > 0 ? DhPublicKey.FromSpan(entity.RemoteEphemeralPub) : null;

        var skippedKeys = DeserializeSkippedKeys(entity.RatchetStateBlob);
        var sessionId = SessionId.Parse(entity.SessionId, null);

        return new DirectRatchetSession(
            new PublicIdentityId(entity.OwnerIdentityId),
            new DeviceId(entity.OwnerDeviceId),
            new PublicIdentityId(entity.RemotePeerId),
            new DeviceId(entity.RemoteDeviceId),
            rootKey,
            sendKey,
            recvKey,
            remotePub,
            localPriv,
            localPub,
            entity.SendingCounter,
            entity.ReceivingCounter,
            sessionId,
            skippedKeys);
    }

    public async Task SaveSessionAsync(DirectRatchetSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var ownerGuid = session.OwnerIdentityId.Value;
        var remoteGuid = session.RemotePeerId.Value;
        var sessionIdStr = session.Id.ToString();

        var existing = await _dbContext.VaultRatchetSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionIdStr, ct);

        var rootBytes = session.RootKey != null ? session.RootKey.Span.ToArray() : Array.Empty<byte>();
        var sendBytes = session.SendingChainKey != null ? session.SendingChainKey.Span.ToArray() : null;
        var recvBytes = session.ReceivingChainKey != null ? session.ReceivingChainKey.Span.ToArray() : null;
        var privBytes = session.LocalEphemeralPrivateKey != null ? session.LocalEphemeralPrivateKey.Span.ToArray() : null;
        var localPubBytes = session.LocalEphemeralPublicKey != null ? session.LocalEphemeralPublicKey.Span.ToArray() : null;
        var remotePubBytes = session.RemoteEphemeralPublicKey != null ? session.RemoteEphemeralPublicKey.Span.ToArray() : null;
        var stateBlob = SerializeSkippedKeys(session.SkippedMessageKeys);

        if (existing == null)
        {
            var entity = new VaultRatchetSessionEntity
            {
                SessionId = sessionIdStr,
                OwnerIdentityId = ownerGuid,
                OwnerDeviceId = session.OwnerDeviceId.Value,
                RemotePeerId = remoteGuid,
                RemoteDeviceId = session.RemoteDeviceId.Value,
                RootKeyCiphertext = rootBytes,
                SendChainKeyCiphertext = sendBytes,
                RecvChainKeyCiphertext = recvBytes,
                LocalEphemeralPrivCiphertext = privBytes,
                LocalEphemeralPub = localPubBytes,
                RemoteEphemeralPub = remotePubBytes,
                SendingCounter = session.SendingCounter,
                ReceivingCounter = session.ReceivingCounter,
                PreviousChainLength = session.PreviousSendingChainLength,
                RatchetStateBlob = stateBlob,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _dbContext.VaultRatchetSessions.AddAsync(entity, ct);
        }
        else
        {
            existing.OwnerIdentityId = ownerGuid;
            existing.OwnerDeviceId = session.OwnerDeviceId.Value;
            existing.RemotePeerId = remoteGuid;
            existing.RemoteDeviceId = session.RemoteDeviceId.Value;
            existing.RootKeyCiphertext = rootBytes;
            existing.SendChainKeyCiphertext = sendBytes;
            existing.RecvChainKeyCiphertext = recvBytes;
            existing.LocalEphemeralPrivCiphertext = privBytes;
            existing.LocalEphemeralPub = localPubBytes;
            existing.RemoteEphemeralPub = remotePubBytes;
            existing.SendingCounter = session.SendingCounter;
            existing.ReceivingCounter = session.ReceivingCounter;
            existing.PreviousChainLength = session.PreviousSendingChainLength;
            existing.RatchetStateBlob = stateBlob;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    private static byte[]? SerializeSkippedKeys(IReadOnlyDictionary<uint, MessageKey> skippedKeys)
    {
        if (skippedKeys == null || skippedKeys.Count == 0) return null;

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(skippedKeys.Count);
        foreach (var (counter, key) in skippedKeys)
        {
            writer.Write(counter);
            writer.Write(key.Span.Length);
            writer.Write(key.Span);
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
